using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Diagnostics;
using DockerDiagram.Helpers;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram
{
    /// <summary>
    /// 애플리케이션의 메인 UI 윈도우(View)입니다.
    /// 마우스 드래그 앤 드롭, 캔버스 패닝(이동) 및 줌, 객체 리사이징, 선 긋기 등 
    /// 사용자의 복잡한 시각적 상호작용(Interaction)을 감지하고 제어하여 MainViewModel로 전달하는 UI 컨트롤 타워 역할을 합니다.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public partial class MainWindow : Window
    {
        // --- 1. 기본 변수들 ---
        // 사이드바에서 아이템을 캔버스로 끌어올 때(Drag & Drop) 사용하는 시작점 및 드래그 상태
        private Point _toolStartPoint;
        private bool _isToolDragging = false;

        // 캔버스 내 일반 그리기(드래그) 시작 좌표
        private Point _startPoint;

        // 이미 캔버스에 배치된 노드를 마우스로 잡고 이동할 때 사용하는 변수들
        private bool _isNodeDragging = false;
        private Point _nodeClickOffset; // 마우스 커서와 노드 좌상단 간의 거리 보정값
        private FrameworkElement? _draggedNodeElement = null;

        // 컨테이너 포트 간 선(Connector)을 직접 그을 때 사용하는 상태 변수들
        private bool _isConnecting = false;
        private IConnectableItem? _sourceItem = null;
        private Point _startPointCanvas;
        private Border? _lastHitPort = null;
        private PortDirection _sourceDir = PortDirection.None;

        // 노드나 그룹의 모서리를 잡아당겨 크기를 조절(Resizing)할 때 사용하는 변수들
        private bool _isResizing = false;
        private string _resizeDir = "";
        private NodeViewModel? _resizingNode = null;
        private Point _resizeStartWorldPos;
        private Rect _resizeStartNodeRect;

        // 마우스 우클릭으로 캔버스 전체 화면을 이동(Panning)시킬 때 사용하는 변수들
        private bool _isPanning = false;
        private Point _panStartClick;
        private Point _panStartTranslate;

        // 상단 멀티 탭(시트) 순서를 드래그해서 바꿀 때 사용하는 상태 변수들
        private Point _sheetDragStartPoint;
        private bool _isSheetDragging = false;
        private bool _isClickedOnTab = false;
        private SheetViewModel? _renamingSheet;

        // 탭 스크롤 좌우 이동 거리 상수
        private const double SCROLL_OFFSET = 400.0;

        // --- 2. 그룹핑(Grouping) 관련 변수 ---
        // 사용자가 직접 화면에 박스를 그려서 그룹/네트워크를 묶는 모드 활성화 플래그
        private bool _isGroupingMode = false;
        private bool _isNetworkDrawingMode = false;

        // 이미 만들어진 그룹 전체를 잡고 이동할 때 사용하는 상태 변수들
        private bool _isGroupMoving = false;
        private GroupViewModel? _movingGroup = null;
        private Point _groupClickOffset;

        // --- 3. 재연결(Reconnection) 관련 변수 ---
        // 이미 그어진 선(Connector)의 끝점을 잡고 다른 곳으로 연결을 옮길 때 사용하는 상태 변수들
        private bool _isReconnecting = false;
        private ConnectorViewModel? _reconnectingConn = null;
        private string _reconnectType = "";

        // 그룹 리사이징 정보 보관용
        private GroupViewModel? _resizingGroup = null;
        private Rect _resizeStartGroupRect;

        // 백그라운드 상태 모니터링 타이머들
        private DispatcherTimer _dockerMonitorTimer;
        private DispatcherTimer _autoSaveTimer;

        // 팝업/알림창 출력을 담당하는 다이얼로그 서비스
        private readonly IDialogService _dialogService;

        // ViewModel에 쉽게 접근하기 위한 헬퍼 프로퍼티
        private MainViewModel ViewModel => (MainViewModel)this.DataContext;

        /// <summary>
        /// MainWindow를 초기화하고 의존성(ViewModel, Service)을 주입받습니다.
        /// 앱 전반의 백그라운드 타이머(도커 상태 감시, 자동 저장)를 세팅합니다.
        /// </summary>
        public MainWindow(MainViewModel viewModel, IDockerService dockerService, IDialogService dialogService)
        {
            InitializeComponent();

            // 1. 다이얼로그 서비스 할당 (Null 체크 유지)
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

            // 2. 데이터 컨텍스트 설정
            this.DataContext = viewModel;

            // 3. 타이머 초기화 (기존 로직 그대로 유지)
            _dockerMonitorTimer = new DispatcherTimer();
            _dockerMonitorTimer.Interval = TimeSpan.FromSeconds(5);
            _dockerMonitorTimer.Tick += DockerMonitorTimer_Tick;

            _autoSaveTimer = new DispatcherTimer();
            _autoSaveTimer.Interval = TimeSpan.FromSeconds(30);
            _autoSaveTimer.Tick += AutoSaveTimer_Tick;
        }

        /// <summary>
        /// 앱이 완전히 종료되기 직전에 호출되는 이벤트입니다.
        /// 작업 중인 캔버스에 저장되지 않은 변경사항(IsModified 플래그)이 있다면 사용자에게 묻고 안전하게 저장(Quick Save)합니다.
        /// </summary>
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (this.DataContext is not MainViewModel vm) return;

            if (!vm.IsModified) return;

            var result = _dialogService.ShowYesNoCancel("변경 사항이 저장되지 않았습니다.\n저장하고 종료하시겠습니까?", "종료 확인");

            if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
            }
            else if (result == MessageBoxResult.Yes)
            {
                if (!string.IsNullOrEmpty(vm.CurrentFilePath))
                {
                    bool saved = FileService.QuickSave(vm, vm.CurrentFilePath);
                    if (!saved) e.Cancel = true;
                }
                else
                {
                    string? savedPath = FileService.SaveDiagramAs(vm, _dialogService);
                    if (string.IsNullOrEmpty(savedPath))
                    {
                        e.Cancel = true;
                    }
                }
            }
        }

        /// <summary>
        /// 설정된 주기(기본 30초)마다 백그라운드에서 조용히 실행되어 작업 내용을 현재 파일에 덮어쓰기 하는 자동 저장 핸들러입니다.
        /// </summary>
        private void AutoSaveTimer_Tick(object? sender, EventArgs e)
        {
            var vm = this.DataContext as MainViewModel;
            if (vm == null) return;

            if (string.IsNullOrEmpty(vm.CurrentFilePath)) return;
            if (!vm.IsModified) return;

            bool success = FileService.QuickSave(vm, vm.CurrentFilePath);
            if (success)
            {
                Debug.WriteLine($"[DockerDiscovery] Saved to {vm.CurrentFilePath} at {DateTime.Now}");
                vm.IsModified = false;
            }
        }

        /// <summary>
        /// 윈도우 UI가 메모리에 성공적으로 로드된 직후 실행됩니다.
        /// 상태 모니터링 타이머들을 시작하고, 도커 프로세스 생존 여부를 최초로 검사합니다.
        /// </summary>
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateScrollButtonsState();

            if (ViewModel == null) return;

            UpdateTitle();

            await CheckDockerStateAsync();

            if (!_dockerMonitorTimer.IsEnabled) _dockerMonitorTimer.Start();
            if (!_autoSaveTimer.IsEnabled) _autoSaveTimer.Start();
        }

        /// <summary>
        /// 백그라운드에서 주기적으로 도커(Docker Desktop) 엔진이 꺼져있는지 감시하는 타이머 핸들러입니다.
        /// </summary>
        private async void DockerMonitorTimer_Tick(object? sender, EventArgs e)
        {
            if (ViewModel.ActiveSheet?.Profile.Type == EndpointType.Local)
            {
                if (DockerServiceHelper.IsDockerRunning()) return;

                _dockerMonitorTimer.Stop();
                await CheckDockerStateAsync();
                _dockerMonitorTimer.Start();
            }
        }

        /// <summary>
        /// 도커 엔진이 꺼져있음을 감지했을 때 사용자에게 알림을 띄우고, 승인 시 도커를 다시 실행시켜주는 자동 복구(Failover) 로직입니다.
        /// </summary>
        private async Task CheckDockerStateAsync()
        {
            if (ViewModel.ActiveSheet?.Profile.Type != EndpointType.Local) return;
            if (DockerServiceHelper.IsDockerRunning()) return;

            bool result = _dialogService.ShowConfirm(
                "내 PC의 Docker 프로세스가 종료되었습니다.\nDocker Desktop을 다시 실행하시겠습니까?\n\n('아니요'를 누르면 프로그램이 종료됩니다.)",
                "Docker 감지");

            if (result)
            {
                try
                {
                    await DockerServiceHelper.StartDockerAsync(ViewModel.ActiveSheet.DockerService, _dialogService);

                    if (DataContext is MainViewModel vm)
                    {
                        await vm.OnDockerStartedAsync();
                    }
                }
                catch (Exception ex)
                {
                    _dialogService.ShowMessage($"Docker 실행 실패: {ex.Message}");
                }
            }
            else
            {
                Application.Current.Shutdown();
            }
        }

        // --- 스크롤 로직 ---

        /// <summary>
        /// 상단 멀티 탭(시트) 목록이 화면을 넘어갈 정도로 길어질 경우, 좌/우 화살표 버튼을 클릭해 탭을 부드럽게 스크롤하는 로직입니다.
        /// </summary>
        private void ScrollLeft_Click(object sender, RoutedEventArgs e)
        {
            double newOffset = Math.Max(0, TabScrollViewer.HorizontalOffset - SCROLL_OFFSET);
            TabScrollViewer.ScrollToHorizontalOffset(newOffset);
        }

        private void ScrollRight_Click(object sender, RoutedEventArgs e)
        {
            double newOffset = Math.Min(TabScrollViewer.ScrollableWidth, TabScrollViewer.HorizontalOffset + SCROLL_OFFSET);
            TabScrollViewer.ScrollToHorizontalOffset(newOffset);
        }

        private void TabScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateScrollButtonsState();
        private void TabScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateScrollButtonsState();

        /// <summary>
        /// 탭 스크롤 위치에 따라 좌/우 이동 화살표 버튼의 활성화(Enable) 상태를 갱신합니다.
        /// </summary>
        private void UpdateScrollButtonsState()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                bool isScrollable = TabScrollViewer.ScrollableWidth > 0;
                bool isAtStart = TabScrollViewer.HorizontalOffset <= 2.0;
                bool isAtEnd = TabScrollViewer.HorizontalOffset >= TabScrollViewer.ScrollableWidth - 2.0;

                if (BtnScrollLeft != null) BtnScrollLeft.IsEnabled = !isAtStart;
                if (BtnScrollRight != null) BtnScrollRight.IsEnabled = !isAtEnd;
            }));
        }

        // --- 좌표 변환 헬퍼 ---

        /// <summary>
        /// 캔버스를 확대/축소(Zoom)하거나 패닝(Panning)했을 때, 화면상의 마우스 클릭 위치를 
        /// 내부 좌표계(World Point) 기준으로 변환하여 정확히 어떤 위치를 클릭했는지 보정해 주는 핵심 헬퍼입니다.
        /// </summary>
        private Point GetWorldPosition(MouseEventArgs e)
        {
            var parent = (UIElement)ZoomPanGrid.Parent;
            Point mouseOnScreen = e.GetPosition(parent);
            var transform = ZoomPanGrid.RenderTransform;
            if (transform != null && transform.Inverse != null)
                return transform.Inverse.Transform(mouseOnScreen);
            return mouseOnScreen;
        }

        // --- 키보드 입력 ---

        /// <summary>
        /// 캔버스 위에서 사용자가 키보드의 'Delete' 키를 눌렀을 때 선택된 항목(노드, 선, 그룹 등)을 즉시 삭제하는 전역 단축키 처리기입니다.
        /// </summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                (DataContext as MainViewModel)?.DeleteCommand.Execute(null);
                e.Handled = true;
            }
        }

        // --- 옵션 팝업 및 기능 버튼 ---

        /// <summary>
        /// 하단(또는 툴바)에 있는 톱니바퀴 모양의 옵션 메뉴를 클릭하여 팝업을 엽니다.
        /// </summary>
        private void OptionButton_Click(object sender, RoutedEventArgs e)
        {
            MapSizePanel.Visibility = Visibility.Collapsed;
            OptionPopup.IsOpen = true;
        }

        /// <summary>
        /// 팝업 메뉴에서 '그룹 모드'를 활성화하거나 비활성화하여, 사용자가 마우스로 도화지에 영역을 그려 노드들을 묶을 수 있게 제어합니다.
        /// 활성화 시 다른 그리기 모드(네트워크 등)를 끄고 십자가 커서로 변경합니다.
        /// </summary>
        private void BtnGroupMode_Checked(object sender, RoutedEventArgs e)
        {
            _isGroupingMode = true;
            _isNetworkDrawingMode = false; // 다른 모드 해제
            Mouse.OverrideCursor = Cursors.Cross;
            OptionPopup.IsOpen = false;
        }

        private void BtnGroupMode_Unchecked(object sender, RoutedEventArgs e)
        {
            _isGroupingMode = false;
            Mouse.OverrideCursor = null;
        }

        /// <summary>
        /// 다이어그램 캔버스의 전체 크기(가로/세로)를 사용자가 직접 설정할 수 있는 '맵 크기 조절 패널'의 가시성을 토글(열기/닫기)합니다.
        /// </summary>
        private void BtnShowMapSize_Click(object sender, RoutedEventArgs e)
        {
            if (MapSizePanel.Visibility == Visibility.Visible)
            {
                MapSizePanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                var vm = DataContext as MainViewModel;
                if (vm?.ActiveSheet != null)
                {
                    txtMapWidth.Text = (vm.ActiveSheet.MapWidth / 10).ToString();
                    txtMapHeight.Text = (vm.ActiveSheet.MapHeight / 10).ToString();
                }
                MapSizePanel.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// 맵 크기 조절 패널 닫기 버튼.
        /// </summary>
        private void CloseMapSize_Click(object sender, RoutedEventArgs e) => MapSizePanel.Visibility = Visibility.Collapsed;

        /// <summary>
        /// 사용자가 입력한 가로/세로 값을 검증한 뒤 현재 활성화된 시트(ActiveSheet)의 실제 맵 크기로 즉시 적용(Apply)합니다.
        /// </summary>
        private void ApplyMapSize_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as MainViewModel;
            if (vm != null && vm.ActiveSheet != null)
            {
                if (double.TryParse(txtMapWidth.Text.Trim(), out double w)) vm.ActiveSheet.MapWidth = w * 10;
                if (double.TryParse(txtMapHeight.Text.Trim(), out double h)) vm.ActiveSheet.MapHeight = h * 10;
                ZoomPanGrid.UpdateLayout();
            }
            MapSizePanel.Visibility = Visibility.Collapsed;
            OptionPopup.IsOpen = false;
        }

        /// <summary>
        /// 다이어그램 캔버스 위에서 마우스 휠을 굴렸을 때 발생하는 이벤트를 가로채어 화면을 줌 인/줌 아웃(확대/축소) 처리합니다.
        /// </summary>
        private void Diagram_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var vm = DataContext as MainViewModel;
            if (vm?.ActiveSheet == null) return;
            e.Handled = true;
            double zoomFactor = e.Delta > 0 ? 1.1 : 0.9;
            vm.ActiveSheet.Scale *= zoomFactor;
        }

        /// <summary>
        /// 캔버스 빈 공간을 마우스 우클릭으로 눌렀을 때, 화면 전체를 이동시키는 패닝(Panning) 모드를 시작하고 마우스를 캡처합니다.
        /// </summary>
        private void Diagram_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isPanning = true;
            _panStartClick = e.GetPosition(this);
            _panStartTranslate = new Point(MapTranslate.X, MapTranslate.Y);
            Mouse.OverrideCursor = Cursors.SizeAll;
            ZoomPanGrid.CaptureMouse();
        }

        /// <summary>
        /// 우클릭을 떼었을 때 패닝(Panning) 모드를 종료하고, 이전 도구 상태(그리기 모드 등)에 맞춰 마우스 커서를 복구합니다.
        /// </summary>
        private void Diagram_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                ZoomPanGrid.ReleaseMouseCapture();
                // 모드에 따라 커서 복구
                if (_isGroupingMode || _isNetworkDrawingMode) Mouse.OverrideCursor = Cursors.Cross;
                else Mouse.OverrideCursor = null;
            }
        }

        /// <summary>
        /// 캔버스 내 마우스 좌클릭 이벤트입니다. 
        /// 사용자가 '그룹/네트워크 그리기 모드'인 경우 클릭한 지점부터 영역 생성을 시작하고(자식 요소로 이벤트 전파 차단),
        /// 일반 모드인 경우 빈 배경을 클릭했는지를 판별하여 현재 선택된 요소를 해제(ClearSelection)합니다.
        /// </summary>
        // 3. 캔버스 클릭 (네트워크/그룹 그리기 시작)
        private void Diagram_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 1. [그리기 모드] (그룹 또는 네트워크)
            // -> 기존 요소(그룹/노드)가 클릭 이벤트를 받기 전에 가로채서 그리기를 시작합니다.
            if (_isGroupingMode || _isNetworkDrawingMode)
            {
                // 화면 좌표 계산
                _startPoint = e.GetPosition(ZoomPanGrid);

                // XAML에 미리 만들어둔 임시 사각형(TempGroupRect) 재사용 및 초기화
                if (TempGroupRect != null)
                {
                    TempGroupRect.Visibility = Visibility.Visible;
                    TempGroupRect.Width = 0;
                    TempGroupRect.Height = 0;
                    Canvas.SetLeft(TempGroupRect, _startPoint.X);
                    Canvas.SetTop(TempGroupRect, _startPoint.Y);

                    if (_isGroupingMode)
                    {
                        // 그룹 스타일 (노란색 점선)
                        TempGroupRect.Stroke = Brushes.Orange;
                        TempGroupRect.StrokeDashArray = new DoubleCollection { 4, 2 };
                        TempGroupRect.Fill = new SolidColorBrush(Color.FromArgb(30, 255, 165, 0));
                    }
                    else if (_isNetworkDrawingMode)
                    {
                        // 네트워크 스타일 (보라색 점선)
                        TempGroupRect.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9B59B6"));
                        TempGroupRect.StrokeDashArray = new DoubleCollection { 2, 2 };
                        TempGroupRect.Fill = new SolidColorBrush(Color.FromArgb(30, 155, 89, 182));
                    }
                }

                // 마우스 가두기 및 이벤트 종료(자식에게 전파 금지)
                ViewportCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            // 2. [일반 모드] 배경 클릭 체크
            // Preview 이벤트는 노드를 클릭해도 실행되므로, 클릭한 대상이 '진짜 배경'인지 확인해야 합니다.
            var clickedElement = e.OriginalSource as DependencyObject;

            // 클릭한 것이 캔버스 배경(ZoomPanGrid)이나 ViewportCanvas, 또는 배경용 흰색 Rectangle인 경우
            if (clickedElement == ZoomPanGrid || clickedElement == ViewportCanvas ||
               (clickedElement is System.Windows.Shapes.Rectangle rect && rect.Fill == Brushes.White))
            {
                // 선택 해제
                (DataContext as MainViewModel)?.ClearSelection();
            }

            // 주의: 일반 모드일 때는 e.Handled = true를 하지 않습니다.
            // 그래야 노드를 클릭했을 때 노드 선택/드래그 로직이 정상 작동합니다.
        }

        /// <summary>
        /// 캔버스 위에서 마우스가 움직일 때 발생하는 모든 드래그 관련 액션을 총괄하는 핵심 라우팅 메서드입니다.
        /// 현재 활성화된 상태 플래그에 따라 '영역 그리기', '화면 패닝', '그룹 이동', '크기 조절(리사이징)', '선 긋기(연결/재연결)' 등 
        /// 적절한 시각적 피드백을 실시간으로 렌더링합니다.
        /// </summary>
        // 4. 캔버스 드래그 (네트워크 사각형 크기 조절 포함)
        private void Diagram_MouseMove(object sender, MouseEventArgs e)
        {
            // 1. [최우선] 그리기 모드 (그룹 또는 네트워크 생성 중)
            if (ViewportCanvas.IsMouseCaptured && (_isGroupingMode || _isNetworkDrawingMode))
            {
                if (TempGroupRect == null) return;

                var currentPoint = e.GetPosition(ZoomPanGrid);
                double x = Math.Min(_startPoint.X, currentPoint.X);
                double y = Math.Min(_startPoint.Y, currentPoint.Y);
                double w = Math.Abs(_startPoint.X - currentPoint.X);
                double h = Math.Abs(_startPoint.Y - currentPoint.Y);

                Canvas.SetLeft(TempGroupRect, x);
                Canvas.SetTop(TempGroupRect, y);
                TempGroupRect.Width = w;
                TempGroupRect.Height = h;

                return;
            }

            var vm = DataContext as MainViewModel;
            if (vm?.ActiveSheet == null) return;

            Point current = GetWorldPosition(e);

            // 2. 화면 패닝 (우클릭 드래그)
            if (_isPanning)
            {
                Point currentMouse = e.GetPosition(this);
                double diffX = currentMouse.X - _panStartClick.X;
                double diffY = currentMouse.Y - _panStartClick.Y;
                vm.ActiveSheet.OffsetX = _panStartTranslate.X + diffX;
                vm.ActiveSheet.OffsetY = _panStartTranslate.Y + diffY;
                return;
            }

            // 3. 그룹 전체 이동
            if (_isGroupMoving && _movingGroup != null)
            {
                double rawTargetX = current.X - _groupClickOffset.X;
                double rawTargetY = current.Y - _groupClickOffset.Y;

                double snappedTargetX = Math.Round(rawTargetX / 10.0) * 10.0;
                double snappedTargetY = Math.Round(rawTargetY / 10.0) * 10.0;

                double dx = snappedTargetX - _movingGroup.X;
                double dy = snappedTargetY - _movingGroup.Y;

                if (Math.Abs(dx) >= 1 || Math.Abs(dy) >= 1)
                {
                    _movingGroup.MoveBy(dx, dy);
                }
                return;
            }

            // 4. 리사이징 (노드 또는 그룹 크기 조절) - ★ 삭제된 것 없이 원상 복구 완료
            if (_isResizing)
            {
                double diffX = current.X - _resizeStartWorldPos.X;
                double diffY = current.Y - _resizeStartWorldPos.Y;

                if (_resizingNode != null)
                {
                    if (_resizeDir.Contains("Right"))
                    {
                        _resizingNode.Width = Math.Max(50, _resizeStartNodeRect.Width + diffX);
                    }
                    if (_resizeDir.Contains("Bottom"))
                    {
                        _resizingNode.Height = Math.Max(50, _resizeStartNodeRect.Height + diffY);
                    }
                    if (_resizeDir.Contains("Left"))
                    {
                        double w = _resizeStartNodeRect.Width - diffX;
                        if (w >= 50)
                        {
                            _resizingNode.X = _resizeStartNodeRect.X + diffX;
                            _resizingNode.Width = w;
                        }
                    }
                    if (_resizeDir.Contains("Top"))
                    {
                        double h = _resizeStartNodeRect.Height - diffY;
                        if (h >= 50)
                        {
                            _resizingNode.Y = _resizeStartNodeRect.Y + diffY;
                            _resizingNode.Height = h;
                        }
                    }
                }
                else if (_resizingGroup != null)
                {
                    Rect contentBounds = GetGroupContentBounds(_resizingGroup);
                    double padding = 20;

                    if (_resizeDir.Contains("Right"))
                    {
                        double minW = (_resizingGroup.ContainedNodes.Count > 0 ? (contentBounds.Right - _resizeStartGroupRect.X) + padding : 50);
                        _resizingGroup.Width = Math.Max(minW, _resizeStartGroupRect.Width + diffX);
                    }
                    if (_resizeDir.Contains("Bottom"))
                    {
                        double minH = (_resizingGroup.ContainedNodes.Count > 0 ? (contentBounds.Bottom - _resizeStartGroupRect.Y) + padding : 50);
                        _resizingGroup.Height = Math.Max(minH, _resizeStartGroupRect.Height + diffY);
                    }
                    if (_resizeDir.Contains("Left"))
                    {
                        double maxAllowedX = _resizingGroup.ContainedNodes.Count > 0 ? contentBounds.Left - padding : _resizeStartGroupRect.Right - 50;
                        double cX = Math.Min(_resizeStartGroupRect.X + diffX, maxAllowedX);
                        _resizingGroup.X = cX;
                        _resizingGroup.Width = _resizeStartGroupRect.Right - cX;
                    }
                    if (_resizeDir.Contains("Top"))
                    {
                        double maxAllowedY = _resizingGroup.ContainedNodes.Count > 0 ? contentBounds.Top - padding : _resizeStartGroupRect.Bottom - 50;
                        double cY = Math.Min(_resizeStartGroupRect.Y + diffY, maxAllowedY);
                        _resizingGroup.Y = cY;
                        _resizingGroup.Height = _resizeStartGroupRect.Bottom - cY;
                    }
                }
                return;
            }

            // ★ 5. 재연결 (그룹 크기를 0x0으로 속여서 직각 선을 정상 작동시킴)
            if (_isReconnecting && _reconnectingConn != null)
            {
                Point startP, endP;
                Rect r1, r2;
                PortDirection d1, d2;

                if (_reconnectType == "Source")
                {
                    startP = current;
                    endP = _reconnectingConn.TargetPos;
                    d1 = PortDirection.None;
                    d2 = _reconnectingConn.TargetDir;
                    r1 = new Rect(current.X, current.Y, 0, 0); // 드래그 중인 임시 지점

                    // 타겟이 그룹이면 알고리즘이 뻗지 않도록 0x0 크기로 전달
                    r2 = _reconnectingConn.Target is GroupViewModel ? new Rect(endP.X, endP.Y, 0, 0) : new Rect(_reconnectingConn.Target.X, _reconnectingConn.Target.Y, _reconnectingConn.Target.Width, _reconnectingConn.Target.Height);
                }
                else
                {
                    startP = _reconnectingConn.SourcePos;
                    endP = current;
                    d1 = _reconnectingConn.SourceDir;
                    d2 = PortDirection.None;

                    // 소스가 그룹이면 알고리즘이 뻗지 않도록 0x0 크기로 전달
                    r1 = _reconnectingConn.Source is GroupViewModel ? new Rect(startP.X, startP.Y, 0, 0) : new Rect(_reconnectingConn.Source.X, _reconnectingConn.Source.Y, _reconnectingConn.Source.Width, _reconnectingConn.Source.Height);
                    r2 = new Rect(current.X, current.Y, 0, 0); // 드래그 중인 임시 지점
                }

                var hitResult = VisualTreeHelper.HitTest(ZoomPanGrid, e.GetPosition(ZoomPanGrid));
                if (hitResult != null)
                {
                    var hitNodeObj = FindParent<FrameworkElement>(hitResult.VisualHit, el => el.DataContext is IConnectableItem);
                    if (hitNodeObj != null && hitNodeObj.DataContext is IConnectableItem hoverItem)
                    {
                        Rect physicalRect = new Rect(hoverItem.X, hoverItem.Y, hoverItem.Width, hoverItem.Height);
                        PortDirection hoverDir = GetClosestDirection(current, physicalRect);
                        Point hoverPoint = GetExactBorderPoint(hoverItem, hoverDir);

                        if (_reconnectType == "Source")
                        {
                            startP = hoverPoint;
                            d1 = hoverDir;
                            r1 = hoverItem is GroupViewModel ? new Rect(startP.X, startP.Y, 0, 0) : physicalRect;
                        }
                        else
                        {
                            endP = hoverPoint;
                            d2 = hoverDir;
                            r2 = hoverItem is GroupViewModel ? new Rect(endP.X, endP.Y, 0, 0) : physicalRect;
                        }
                    }
                }

                try
                {
                    var route = OrthogonalRouter.GetRoute(startP, d1, endP, d2, r1, r2);
                    TempPolyline.Points = (route != null && route.Count >= 2) ? route : new PointCollection { startP, endP };
                }
                catch
                {
                    TempPolyline.Points = new PointCollection { startP, endP };
                }
                return;
            }

            // ★ 6. 신규 연결 (그룹 크기를 0x0으로 속여서 직각 선을 정상 작동시킴)
            if (_isConnecting && _sourceItem != null)
            {
                Point startP = GetExactBorderPoint(_sourceItem, _sourceDir);
                PortDirection targetDir = PortDirection.None;

                // 출발지가 그룹이면 투명 껍데기(0x0)로 처리
                Rect sourceRect = _sourceItem is GroupViewModel ? new Rect(startP.X, startP.Y, 0, 0) : new Rect(_sourceItem.X, _sourceItem.Y, _sourceItem.Width, _sourceItem.Height);
                Rect targetRect = new Rect(current.X, current.Y, 0, 0);
                Point endPoint = current;

                if (_lastHitPort != null)
                {
                    _lastHitPort.Background = Brushes.Transparent;
                    _lastHitPort = null;
                }

                var hitResult = VisualTreeHelper.HitTest(ZoomPanGrid, e.GetPosition(ZoomPanGrid));
                if (hitResult != null)
                {
                    var hitNodeObj = FindParent<FrameworkElement>(hitResult.VisualHit, el => el.DataContext is IConnectableItem);
                    if (hitNodeObj != null && hitNodeObj.DataContext is IConnectableItem targetItem && targetItem != _sourceItem)
                    {
                        Rect physicalRect = new Rect(targetItem.X, targetItem.Y, targetItem.Width, targetItem.Height);
                        targetDir = GetClosestDirection(current, physicalRect);
                        endPoint = GetExactBorderPoint(targetItem, targetDir);

                        // 도착지가 그룹이면 투명 껍데기(0x0)로 처리
                        targetRect = targetItem is GroupViewModel ? new Rect(endPoint.X, endPoint.Y, 0, 0) : physicalRect;
                    }
                }

                try
                {
                    var route = OrthogonalRouter.GetRoute(startP, _sourceDir, endPoint, targetDir, sourceRect, targetRect);
                    TempPolyline.Points = (route != null && route.Count >= 2) ? route : new PointCollection { startP, endPoint };
                }
                catch
                {
                    TempPolyline.Points = new PointCollection { startP, endPoint };
                }
            }
        }

        /// <summary>
        /// 캔버스 위에서 마우스 왼쪽 버튼을 뗐을 때 호출되는 최종 이벤트 핸들러입니다.
        /// 드래그로 진행 중이던 시각적 상호작용(영역 그리기, 노드/그룹 이동, 연결선 긋기, 리사이징 등)을 확정하고,
        /// 계산된 최종 좌표나 상태를 뷰모델(ViewModel)의 비즈니스 로직에 반영하여 실제 데이터 모델을 업데이트합니다.
        /// </summary>
        // 5. 마우스 뗌 (네트워크/그룹 생성 완료)
        private async void Diagram_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isClickedOnTab = false;

            // 1. [그리기 모드 완료] (그룹 또는 네트워크)
            if (ViewportCanvas.IsMouseCaptured && (_isGroupingMode || _isNetworkDrawingMode))
            {
                ViewportCanvas.ReleaseMouseCapture();

                // 임시 도형 숨기기
                if (TempGroupRect != null) TempGroupRect.Visibility = Visibility.Collapsed;
                if (TempPolyline != null) TempPolyline.Visibility = Visibility.Collapsed;

                var vm = DataContext as MainViewModel;

                // 임시 사각형이 있고 ViewModel이 연결된 경우 생성 로직 실행
                if (vm != null && TempGroupRect != null)
                {
                    double w = TempGroupRect.Width;
                    double h = TempGroupRect.Height;
                    double x = Canvas.GetLeft(TempGroupRect);
                    double y = Canvas.GetTop(TempGroupRect);

                    // 너무 작게(단순 클릭) 한 경우 생성 방지 (최소 20x20)
                    if (w > 20 && h > 20)
                    {
                        // A) 그룹 생성
                        if (_isGroupingMode && vm.ActiveSheet?.DockerService is INetworkService netService)
                        {
                            var newGroup = new GroupViewModel(x, y, w, h, netService, _dialogService);
                            newGroup.ParentSheet = vm.ActiveSheet;
                            vm.ActiveSheet.Groups.Add(newGroup);
                            vm.ActiveSheet.RefreshGroupContainment(newGroup);
                            vm.SelectedElement = newGroup;

                            vm.ActiveSheet.UpdateGroupLayering();
                        }
                        // B) 네트워크 생성
                        else if (_isNetworkDrawingMode)
                        {
                            var dlg = new Views.NetworkDialog(_dialogService);
                            dlg.Owner = this;
                            if (dlg.ShowDialog() == true)
                            {
                                // MainViewModel의 함수 내부에서 이미 UpdateGroupLayering을 호출하도록 수정했으므로 여기선 호출만 하면 됨
                                await vm.CreateNewNetworkGroupAsync(dlg.NetworkName, dlg.Driver, x, y, w, h);
                            }
                        }
                    }
                }

                // 모드 초기화 및 커서 복구
                _isGroupingMode = false;
                _isNetworkDrawingMode = false;
                Mouse.OverrideCursor = null;

                e.Handled = true;
                return;
            }

            // --- 기존 기능들 ---

            // 2. 그룹 이동 종료
            if (_isGroupMoving)
            {
                _isGroupMoving = false;
                Mouse.Capture(null);
                _movingGroup = null;
            }

            // 3. 노드 드래그 종료
            if (_isNodeDragging)
            {
                if (_draggedNodeElement != null && _draggedNodeElement.DataContext is NodeViewModel nodeVm)
                {
                    var sheet = (DataContext as MainViewModel)?.ActiveSheet;
                    if (sheet != null)
                    {
                        // 이동이 끝난 노드의 위치를 기반으로 어떤 그룹 영역에 포함되는지 검사하여 자동 소속 처리
                        var targetGroups = sheet.FindGroupsAt(nodeVm.X, nodeVm.Y, nodeVm.Width, nodeVm.Height);

                        foreach (var group in sheet.Groups)
                        {
                            if (targetGroups.Contains(group))
                            {
                                group.AddNode(nodeVm);
                            }
                            else
                            {
                                group.RemoveNode(nodeVm);
                            }
                        }
                    }
                    _draggedNodeElement.ReleaseMouseCapture();
                }
                _isNodeDragging = false;
                _draggedNodeElement = null;
            }

            // ★ 4. 재연결 종료 (IConnectableItem 적용)
            if (_isReconnecting && _reconnectingConn != null)
            {
                _isReconnecting = false;
                (Mouse.Captured as FrameworkElement)?.ReleaseMouseCapture();
                if (TempPolyline != null) TempPolyline.Visibility = Visibility.Collapsed;

                // 히트 테스트 및 연결 업데이트
                var hitResult = VisualTreeHelper.HitTest(ZoomPanGrid, e.GetPosition(ZoomPanGrid));
                if (hitResult != null)
                {
                    var hitNodeObj = FindParent<FrameworkElement>(hitResult.VisualHit, x => x.DataContext is IConnectableItem);
                    if (hitNodeObj != null && hitNodeObj.DataContext is IConnectableItem hitItem)
                    {
                        Rect nodeRect = new Rect(hitItem.X, hitItem.Y, hitItem.Width, hitItem.Height);
                        PortDirection newDir = GetClosestDirection(GetWorldPosition(e), nodeRect);

                        if (_reconnectType == "Source")
                            _reconnectingConn.UpdateConnection(hitItem, newDir, _reconnectingConn.Target, _reconnectingConn.TargetDir);
                        else
                            _reconnectingConn.UpdateConnection(_reconnectingConn.Source, _reconnectingConn.SourceDir, hitItem, newDir);
                    }
                }
                _reconnectingConn = null;
                return;
            }

            // 5. 리사이징 종료
            if (_isResizing)
            {
                _isResizing = false;
                (Mouse.Captured as FrameworkElement)?.ReleaseMouseCapture();

                if (_resizingGroup != null)
                {
                    var vm = DataContext as MainViewModel;
                    vm?.ActiveSheet?.UpdateGroupLayering();
                }

                _resizingNode = null;
                _resizingGroup = null;
            }

            // ★ 6. 신규 연결 종료 (IConnectableItem 적용 및 _sourceItem 사용)
            if (_isConnecting)
            {
                _isConnecting = false;
                Mouse.Capture(null);
                if (TempPolyline != null) TempPolyline.Visibility = Visibility.Collapsed;
                if (_lastHitPort != null) { _lastHitPort.Background = Brushes.Transparent; _lastHitPort = null; }

                var hitResult = VisualTreeHelper.HitTest(ZoomPanGrid, e.GetPosition(ZoomPanGrid));
                if (hitResult != null)
                {
                    var hitNodeObj = FindParent<FrameworkElement>(hitResult.VisualHit, el => el.DataContext is IConnectableItem);
                    if (hitNodeObj != null && hitNodeObj.DataContext is IConnectableItem targetItem)
                    {
                        // 그룹은 IsCreating 속성이 없으므로, NodeViewModel일 때만 확인하도록 캐스팅 처리
                        bool isCreating = (targetItem as NodeViewModel)?.IsCreating ?? false;

                        if (!isCreating && targetItem != _sourceItem && _sourceItem != null)
                        {
                            Rect targetRect = new Rect(targetItem.X, targetItem.Y, targetItem.Width, targetItem.Height);
                            PortDirection targetDir = GetClosestDirection(GetWorldPosition(e), targetRect);
                            (DataContext as MainViewModel)?.AddConnection(_sourceItem, targetItem, _sourceDir, targetDir);
                        }
                    }
                }
                _sourceItem = null;
            }
        }

        // --- 기타 UI 이벤트들 ---

        /// <summary>
        /// 그룹의 헤더(상단 제목 영역)를 마우스로 눌렀을 때, 그룹 전체 이동(드래그) 모드를 활성화합니다.
        /// </summary>
        private void GroupHeader_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement border) return;
            _movingGroup = border.DataContext as GroupViewModel;

            if (_movingGroup != null)
            {
                _isGroupMoving = true;
                Point mouseWorld = GetWorldPosition(e);
                _groupClickOffset = new Point(mouseWorld.X - _movingGroup.X, mouseWorld.Y - _movingGroup.Y);
                border.CaptureMouse();
                e.Handled = true;
            }
        }

        /// <summary>
        /// 그룹의 본문(배경 영역)을 클릭했을 때 해당 그룹을 선택 상태로 전환합니다.
        /// </summary>
        private void GroupBody_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var rect = sender as FrameworkElement;
            var groupVm = rect?.DataContext as GroupViewModel;
            var vm = DataContext as MainViewModel;
            if (vm != null && groupVm != null)
            {
                vm.SelectedElement = groupVm;
            }
            e.Handled = true;
        }

        /// <summary>
        /// 연결선의 양 끝에 위치한 그립(원형 핸들)을 클릭했을 때, 
        /// 기존 선을 떼어내어 다른 대상에게 연결하는 '재연결(Reconnection)' 모드를 시작합니다.
        /// </summary>
        private void ConnectorGrip_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Ellipse ellipse) return;
            if (ellipse.DataContext is not ConnectorViewModel connVm) return;
            _reconnectingConn = connVm;

            if (ellipse.Tag is string tag) _reconnectType = tag;
            else _reconnectType = string.Empty;

            _isReconnecting = true;
            TempPolyline.Visibility = Visibility.Visible;
            TempPolyline.Points = _reconnectingConn.Points;

            ellipse.CaptureMouse();
            e.Handled = true;
        }

        /// <summary>
        /// 상단 다이어그램 탭(시트)을 마우스로 클릭했을 때, 탭 순서를 변경하기 위한 드래그 앤 드롭 준비 상태에 돌입합니다.
        /// </summary>
        private void SheetTab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _sheetDragStartPoint = e.GetPosition(null);
            _isSheetDragging = false;
            _isClickedOnTab = true;
        }

        /// <summary>
        /// 탭을 클릭한 상태로 일정 거리 이상 이동하면 드래그 앤 드롭 이벤트를 발생시켜 탭의 순서 변경 작업을 시작합니다.
        /// </summary>
        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            base.OnPreviewMouseMove(e);
            if (e.LeftButton == MouseButtonState.Pressed && !_isSheetDragging && _isClickedOnTab)
            {
                var point = e.GetPosition(null);
                // 마우스가 클릭된 상태에서 10픽셀 이상 움직이면 드래그로 간주
                if (Math.Abs(point.X - _sheetDragStartPoint.X) > 10 || Math.Abs(point.Y - _sheetDragStartPoint.Y) > 10)
                {
                    var hitElem = VisualTreeHelper.HitTest(SheetListBox, e.GetPosition(SheetListBox))?.VisualHit;
                    var listBoxItem = FindParent<ListBoxItem>(hitElem, x => true);
                    if (listBoxItem != null && listBoxItem.DataContext is SheetViewModel sheet)
                    {
                        _isSheetDragging = true;
                        DragDrop.DoDragDrop(SheetListBox, new DataObject("SheetData", sheet), DragDropEffects.Move);
                        _isSheetDragging = false;
                        _isClickedOnTab = false;
                    }
                }
            }
        }

        /// <summary>
        /// 드래그 중인 탭을 다른 탭 위에 떨어뜨렸을 때(Drop), 뷰모델에 순서 변경(MoveSheet)을 요청하여 UI에 반영합니다.
        /// </summary>
        private void SheetTab_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("SheetData"))
            {
                var sourceSheet = e.Data.GetData("SheetData") as SheetViewModel;
                var vm = DataContext as MainViewModel;
                var pos = e.GetPosition(SheetListBox);
                var result = VisualTreeHelper.HitTest(SheetListBox, pos);

                if (result != null && sourceSheet != null && vm != null)
                {
                    var targetItem = FindParent<ListBoxItem>(result.VisualHit, x => true);
                    if (targetItem != null && targetItem.DataContext is SheetViewModel targetSheet)
                    {
                        int oldIdx = vm.Sheets.IndexOf(sourceSheet!);
                        int newIdx = vm.Sheets.IndexOf(targetSheet);
                        vm.MoveSheet(oldIdx, newIdx);
                    }
                }
            }
        }

        /// <summary>
        /// 시트 이름 변경(Rename) 팝업을 띄우는 이벤트 핸들러입니다.
        /// 시트 탭의 컨텍스트 메뉴에서 호출되며, 현재 시트의 이름을 텍스트 박스에 로드하고 포커스를 줍니다.
        /// </summary>
        private void RenameSheet_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var contextMenu = menuItem?.Parent as ContextMenu;
            var border = contextMenu?.PlacementTarget as FrameworkElement;
            if (border?.DataContext is SheetViewModel sheet)
            {
                _renamingSheet = sheet;
                txtRename.Text = sheet.Title;
                RenameOverlay.Visibility = Visibility.Visible;
                txtRename.Focus();
                txtRename.SelectAll();
            }
        }

        /// <summary>
        /// 시트 이름 변경을 확정(OK)하고 팝업을 닫습니다.
        /// </summary>
        private void RenameOK_Click(object sender, RoutedEventArgs e)
        {
            if (_renamingSheet != null && !string.IsNullOrWhiteSpace(txtRename.Text))
                (DataContext as MainViewModel)?.RenameSheet(_renamingSheet, txtRename.Text.Trim());
            RenameOverlay.Visibility = Visibility.Collapsed;
            _renamingSheet = null;
        }

        /// <summary>
        /// 시트 이름 변경을 취소하고 팝업을 닫습니다.
        /// </summary>
        private void RenameCancel_Click(object sender, RoutedEventArgs e)
        {
            RenameOverlay.Visibility = Visibility.Collapsed;
            _renamingSheet = null;
        }

        /// <summary>
        /// 시트 탭의 컨텍스트 메뉴에서 시트 삭제를 클릭했을 때 호출됩니다.
        /// </summary>
        private void DeleteSheet_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var contextMenu = menuItem?.Parent as ContextMenu;
            var border = contextMenu?.PlacementTarget as FrameworkElement;
            if (border?.DataContext is SheetViewModel sheet)
                (DataContext as MainViewModel)?.DeleteSheet(sheet);
        }

        /// <summary>
        /// 탭 영역 오른쪽에 위치한 전체 시트 목록 보기 드롭다운 버튼을 클릭했을 때 호출됩니다.
        /// </summary>
        private void SheetMenuButton_Click(object sender, RoutedEventArgs e) => SheetMenuPopup.IsOpen = true;

        /// <summary>
        /// 전체 시트 목록 드롭다운에서 특정 시트를 선택하면, 즉시 해당 시트로 이동하고 팝업을 닫습니다.
        /// </summary>
        private void SheetMenuList_SelectionChanged(object sender, SelectionChangedEventArgs e) => SheetMenuPopup.IsOpen = false;

        /// <summary>
        /// 시트가 변경될 때, 탭 스크롤 영역에서 선택된 탭이 화면에 보이도록(ScrollIntoView) 스크롤을 자동 조절합니다.
        /// </summary>
        private void SheetListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SheetListBox.SelectedItem != null) SheetListBox.ScrollIntoView(SheetListBox.SelectedItem);
        }

        /// <summary>
        /// 왼쪽 사이드바 도구(Tool)를 마우스로 눌렀을 때 호출됩니다.
        /// 사용자가 버튼을 클릭한 것인지, 아니면 드래그 앤 드롭을 시작하려는 것인지 구분하기 위해 시작 좌표를 기록합니다.
        /// 네트워크나 그룹 도구의 경우, 캔버스 그리기 모드로 즉시 전환하고 이벤트를 소비합니다.
        /// </summary>
        // 1. 사이드바 아이콘 클릭 (네트워크면 그리기 모드 진입)
        private void Tool_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 1. 드래그 시작 위치 저장 및 초기화
            _toolStartPoint = e.GetPosition(null);
            _isToolDragging = false;

            // 2. 클릭한 도구의 타입 확인 (Tag 속성 가져오기)
            var border = sender as Border;
            string typeStr = border?.Tag?.ToString() ?? "";

            // 1) Network 버튼 클릭
            if (typeStr == "Network")
            {
                _isNetworkDrawingMode = true;
                _isGroupingMode = false;       // 그룹 모드 끄기
                Mouse.OverrideCursor = Cursors.Cross; // 십자가 커서
                (DataContext as MainViewModel)?.ClearSelection();
                e.Handled = true; // 이벤트 소비 (DoDragDrop 방지)
                return;
            }

            // 2) Group 버튼 클릭
            if (typeStr == "Group")
            {
                _isGroupingMode = true;
                _isNetworkDrawingMode = false; // 네트워크 모드 끄기
                Mouse.OverrideCursor = Cursors.Cross; // 십자가 커서
                (DataContext as MainViewModel)?.ClearSelection();
                e.Handled = true; // 이벤트 소비 (DoDragDrop 방지)
                return;
            }

            _isNetworkDrawingMode = false;
            _isGroupingMode = false;
            Mouse.OverrideCursor = null;   // 커서 원래대로 (화살표)
        }

        /// <summary>
        /// 왼쪽 사이드바에서 마우스를 누른 채 이동할 때 호출됩니다.
        /// 일정 거리(5픽셀) 이상 움직이면 '드래그 앤 드롭'의 시작으로 간주하고,
        /// 선택한 도구(컨테이너, 볼륨, 템플릿 등)의 타입에 맞는 DataObject를 생성하여 OS 수준의 드래그 파이프라인(DoDragDrop)에 태워 보냅니다.
        /// </summary>
        // 2. 마우스 이동 (네트워크 모드면 드래그 앤 드롭 방지 + 기존 목록 드래그 처리)
        private void Tool_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isNetworkDrawingMode) return; // 네트워크 그리기 모드 중이면 중단

            if (e.LeftButton == MouseButtonState.Pressed && !_isToolDragging)
            {
                if (Math.Abs(e.GetPosition(null).X - _toolStartPoint.X) > 5)
                {
                    _isToolDragging = true;
                    var border = sender as Border;
                    if (border == null) return;

                    // 드래그할 데이터 객체 준비
                    DockerResource container = null;

                    // [CASE 1] 템플릿 목록에서 드래그 (DataContext가 TemplateItem인 경우)
                    if (border.DataContext is TemplateItem template)
                    {
                        container = new DockerContainer
                        {
                            Name = template.Name,
                            Image = template.Image,
                            Id = "" // New임을 표시
                        };
                    }
                    // [CASE 2] 상단 고정 버튼에서 드래그 (Tag가 문자열인 경우)
                    else if (border.Tag is string tagStr)
                    {
                        if (tagStr == "Container")
                        {
                            container = new DockerContainer
                            {
                                Name = "New Container",
                                Image = "New Container",
                                Id = ""
                            };
                        }
                        else if (tagStr == "Volume")
                        {
                            // Canvas_Drop에서 DockerContainer로 캐스팅해서 받으므로 타입을 맞춰줍니다.
                            container = new DockerVolume
                            {
                                Name = "New Volume",
                                Id = ""
                            };
                        }
                        else if (tagStr == "Internet")
                        {
                            container = new DockerInternet
                            {
                                Name = "Internet",
                                Id = ""
                            };
                        }
                    }

                    // 데이터가 준비되었으면 드래그 시작
                    if (container != null)
                    {
                        DataObject data = new DataObject("DockerContainerObject", container);
                        DragDrop.DoDragDrop(border, data, DragDropEffects.Copy);
                    }

                    _isToolDragging = false;
                }
            }
        }

        /// <summary>
        /// 왼쪽 사이드바 도구를 드래그하지 않고 단순히 '클릭(Click)'하고 마우스를 뗐을 때 호출됩니다.
        /// 드래그 앤 드롭 없이 캔버스 기본 위치(200, 200)에 즉시 요소를 생성하는 다이얼로그 팝업(ContainerDialog, VolumeDialog 등)을 띄워줍니다.
        /// </summary>
        private async void Tool_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isToolDragging)
            {
                var border = sender as Border;
                string typeStr = border?.Tag?.ToString() ?? "";

                if (typeStr == "Network" || typeStr == "Group") return;

                if (Enum.TryParse(typeStr, out NodeType type))
                {
                    var vm = DataContext as MainViewModel;
                    if (vm != null)
                    {
                        double defaultX = 200;
                        double defaultY = 200;

                        if (type == NodeType.Container)
                        {
                            var dlg = new Views.ContainerDialog(_dialogService);
                            dlg.Owner = this;

                            if (dlg.ShowDialog() == true)
                            {
                                try
                                {
                                    Mouse.OverrideCursor = Cursors.Wait;

                                    // ==========================================
                                    // ★ 탭 선택에 따른 분기 처리
                                    // ==========================================
                                    if (dlg.SelectedCreationMode == 0) // 1. [🎛️ 직접 설정 (UI)] 탭
                                    {
                                        // 이미지 이름과 태그(:)를 안전하게 분리
                                        string fullImage = dlg.ImageName.Contains(":") ? dlg.ImageName : dlg.ImageName + ":latest";
                                        var parts = fullImage.Split(new[] { ':' }, 2);

                                        // ★ 끝에 dlg.Command, dlg.IsInteractive 정식으로 추가!
                                        await vm.CreateNewContainerNodeAsync(
                                            dlg.ContainerName, parts[0], parts.Length > 1 ? parts[1] : "latest",
                                            dlg.Ports, dlg.EnvVars,
                                            dlg.Volumes, dlg.RestartPolicy, dlg.MemoryMb, dlg.CpuCount, defaultX, defaultY,
                                            dlg.SelectedNetwork, dlg.Command, dlg.IsInteractive);
                                    }
                                    else if (dlg.SelectedCreationMode == 1) // 2. [💻 명령어로 생성 (CLI)] 탭
                                    {
                                        // 하이브리드 파싱 로직 호출!
                                        await vm.ProcessCliCommandAsync(dlg.CliCommand, defaultX, defaultY);
                                    }
                                    else if (dlg.SelectedCreationMode == 2) // 3. [🛠️ 도커파일로 빌드] 탭
                                    {
                                        await vm.BuildImageAndCreateNodeAsync(dlg.BuildImageTag, dlg.DockerfileContent, dlg.UploadedDockerfilePath, defaultX, defaultY);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _dialogService.ShowMessage($"에러 : {ex.Message}");
                                }
                                finally
                                {
                                    Mouse.OverrideCursor = null;
                                }
                            }
                        }
                        else if (type == NodeType.Volume)
                        {
                            var dlg = new Views.VolumeDialog(_dialogService);
                            dlg.Owner = this;
                            if (dlg.ShowDialog() == true)
                            {
                                await vm.CreateNewVolumeNodeAsync(dlg.VolumeName, dlg.Driver, defaultX, defaultY);
                            }
                        }
                        else if (type == NodeType.Internet)
                        {
                            // ★ [수정됨] CreateInternetAt 대신 통합된 CreateNodeAt 사용!
                            vm.ActiveSheet.CreateNodeAt(new DockerInternet { Name = "Internet" }, defaultX, defaultY);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 사이드바에서 드래그한 도구나 기존 도커 리소스를 다이어그램 캔버스(도화지)에 떨어뜨렸을 때(Drop) 발생하는 핵심 이벤트입니다.
        /// 드롭된 화면 좌표를 캔버스 내부 좌표(World Point)로 변환하고 10px 단위 그리드에 스냅(Snap)한 뒤,
        /// 데이터 타입과 생성 모드(새로 만들기 vs 기존 요소 배치)에 따라 알맞은 다이얼로그를 띄우거나 노드를 즉시 배치합니다.
        /// </summary>
        // 캔버스 드롭 핸들러 (네트워크 삭제 반영)
        private async void Canvas_Drop(object sender, DragEventArgs e)
        {
            var vm = DataContext as MainViewModel;
            if (vm?.ActiveSheet == null) return;

            // 마우스 드롭 좌표를 캔버스 내부 좌표로 변환 및 10단위 스냅(Snap) 처리
            Point mouseOnScreen = e.GetPosition((UIElement)ZoomPanGrid.Parent);
            Point worldPos = ZoomPanGrid.RenderTransform.Inverse.Transform(mouseOnScreen);
            double snapX = Math.Round((worldPos.X - 80) / 10) * 10;
            double snapY = Math.Round((worldPos.Y - 40) / 10) * 10;

            bool needsLayerUpdate = false; // 레이어 업데이트 필요 여부 체크

            // [CASE A] 모든 노드 리소스 처리 (DockerResource로 받기)
            if (e.Data.GetDataPresent("DockerContainerObject"))
            {
                var d = e.Data.GetData("DockerContainerObject") as DockerResource;
                if (d == null) return;

                if (string.IsNullOrEmpty(d.Id)) // "New" 아이템 (새로 생성해야 하는 객체)
                {
                    if (d is DockerContainer container)
                    {
                        var dlg = new Views.ContainerDialog(_dialogService);
                        dlg.Owner = this;
                        if (container.Image != "New Container") dlg.ImageName = container.Image;

                        if (dlg.ShowDialog() == true)
                        {
                            try
                            {
                                Mouse.OverrideCursor = Cursors.Wait;

                                // ==========================================
                                // ★ [수정된 부분] 탭 선택에 따라 분기 처리!
                                // ==========================================
                                if (dlg.SelectedCreationMode == 0) // 1. [🎛️ 직접 설정 (UI)] 탭
                                {
                                    string fullImage = dlg.ImageName.Contains(":") ? dlg.ImageName : dlg.ImageName + ":latest";
                                    var parts = fullImage.Split(new[] { ':' }, 2);

                                    // 기존 코드 (※ 끝에 Command와 IsInteractive 값도 같이 넘겨주면 완벽합니다!)
                                    await vm.CreateNewContainerNodeAsync(dlg.ContainerName, parts[0], parts.Length > 1 ? parts[1] : "latest",
                                        dlg.Ports, dlg.EnvVars, dlg.Volumes, dlg.RestartPolicy, dlg.MemoryMb, dlg.CpuCount, snapX, snapY,
                                        dlg.SelectedNetwork, dlg.Command, dlg.IsInteractive);
                                }
                                else if (dlg.SelectedCreationMode == 1) // 2. [💻 명령어로 생성 (CLI)] 탭
                                {
                                    // 방금 MainViewModel에 만든 '하이브리드 파싱' 메서드 호출!
                                    await vm.ProcessCliCommandAsync(dlg.CliCommand, snapX, snapY);
                                }
                                else if (dlg.SelectedCreationMode == 2) // 3. [🛠️ 도커파일로 빌드] 탭
                                {
                                    // defaultX, defaultY -> snapX, snapY 로 변경!
                                    await vm.BuildImageAndCreateNodeAsync(dlg.BuildImageTag, dlg.DockerfileContent, dlg.UploadedDockerfilePath, snapX, snapY);
                                }
                            }
                            catch (Exception ex)
                            {
                                _dialogService.ShowError($"에러 : {ex.Message}", "오류");
                            }
                            finally { Mouse.OverrideCursor = null; }
                        }
                    }
                    else if (d is DockerVolume volume)
                    {
                        if (volume.Name == "New Volume")
                        {
                            var dlg = new Views.VolumeDialog(_dialogService);
                            dlg.Owner = this;
                            if (dlg.ShowDialog() == true)
                            {
                                await vm.CreateNewVolumeNodeAsync(dlg.VolumeName, dlg.Driver, snapX, snapY);
                            }
                        }
                        else
                        {
                            await vm.CreateNodeAtAsync(volume, snapX, snapY);
                        }
                    }
                    else if (d is DockerInternet internet)
                    {
                        vm.ActiveSheet.CreateNodeAt(new DockerInternet { Name = "Internet" }, snapX, snapY);
                    }
                }
                else // 기존 아이템 (이미 도커에 존재하는 객체를 화면에만 꺼내는 경우)
                {
                    if (d is DockerContainer existingContainer)
                    {
                        // 도커 상태값에 맞춰 노드 상태 색상을 갱신
                        if (string.Equals(existingContainer.State, "running", StringComparison.OrdinalIgnoreCase)) existingContainer.StateColor = "#28a745";
                        else if (string.Equals(existingContainer.State, "exited", StringComparison.OrdinalIgnoreCase) || string.Equals(existingContainer.State, "dead", StringComparison.OrdinalIgnoreCase)) existingContainer.StateColor = "#dc3545";
                        else existingContainer.StateColor = "#808080";
                    }
                    await vm.CreateNodeAtAsync(d, snapX, snapY);
                }
            }
            // [CASE B] 네트워크 그룹 (DockerGroupObject)
            else if (e.Data.GetDataPresent("DockerGroupObject"))
            {
                var group = e.Data.GetData("DockerGroupObject") as DockerNetworkGroup;
                if (group != null)
                {
                    await vm.CreateNodeAtAsync(group, snapX, snapY);
                    needsLayerUpdate = true;
                }
            }

            if (needsLayerUpdate)
            {
                vm.ActiveSheet.UpdateGroupLayering();
            }
        }

        /// <summary>
        /// 사이드바의 '실제 도커 리소스(컨테이너, 볼륨, 네트워크)' 목록에서 항목을 마우스로 드래그할 때 호출됩니다.
        /// 선택된 항목의 타입에 따라 알맞은 모델 데이터를 `DataObject`로 포장하여 시스템의 드래그 앤 드롭 파이프라인에 전달합니다.
        /// </summary>
        // ExistingItem_MouseMove 수정 (네트워크일 경우 DockerGroup으로 포장)
        private void ExistingItem_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isToolDragging)
            {
                if (Math.Abs(e.GetPosition(null).X - _toolStartPoint.X) > 5)
                {
                    _isToolDragging = true;
                    var border = sender as Border;

                    // 1. 컨테이너인 경우
                    if (border?.DataContext is DockerContainer container)
                    {
                        DataObject data = new DataObject("DockerContainerObject", container);
                        DragDrop.DoDragDrop(border, data, DragDropEffects.Copy);
                    }
                    // 2. 볼륨인 경우 (이 부분이 누락되어 드래그가 안 됐던 것입니다)
                    else if (border?.DataContext is DockerVolume volume)
                    {
                        // Canvas_Drop에서 리소스를 통합 판단하므로 키를 "DockerContainerObject"로 동일하게 맞춥니다.
                        DataObject data = new DataObject("DockerContainerObject", volume);
                        DragDrop.DoDragDrop(border, data, DragDropEffects.Copy);
                    }
                    // 3. 네트워크(그룹)인 경우
                    else if (border?.DataContext is DockerNetworkGroup group)
                    {
                        DataObject data = new DataObject("DockerGroupObject", group);
                        DragDrop.DoDragDrop(border, data, DragDropEffects.Copy);
                    }

                    _isToolDragging = false;
                }
            }
        }

        /// <summary>
        /// 사이드바의 '템플릿' 목록에서 항목을 드래그할 때 호출됩니다.
        /// 해당 템플릿의 이미지 정보를 바탕으로 신규 컨테이너 생성을 위한 데이터를 준비하여 드래그를 시작합니다.
        /// </summary>
        // 기존 템플릿 아이템 드래그
        private void TemplateItem_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isToolDragging)
            {
                if (Math.Abs(e.GetPosition(null).X - _toolStartPoint.X) > 5)
                {
                    _isToolDragging = true;
                    var border = sender as Border;
                    if (border != null && border.DataContext is TemplateItem template)
                    {
                        var container = new DockerContainer
                        {
                            Name = template.Name,
                            Image = template.Image,
                        };

                        DataObject data = new DataObject("DockerContainerObject", container);
                        DragDrop.DoDragDrop(border, data, DragDropEffects.Copy);

                        _isToolDragging = false;
                    }
                }
            }
        }

        /// <summary>
        /// 기존 도커 리소스 항목 드래그 시작 전, 마우스 클릭 지점을 기억하여 미세한 흔들림으로 인한 오작동을 방지(드래그 임계값 계산용)합니다.
        /// </summary>
        private void ExistingItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _toolStartPoint = e.GetPosition(null);
            _isToolDragging = false;
        }

        /// <summary>
        /// 템플릿 항목 드래그 시작 전 클릭 지점을 기록합니다.
        /// </summary>
        private void TemplateItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _toolStartPoint = e.GetPosition(null);
            _isToolDragging = false;
        }

        /// <summary>
        /// 노드나 네트워크 그룹 객체의 모서리(크기 조절 그립)를 마우스로 눌렀을 때 호출됩니다.
        /// 리사이징 모드를 활성화하고 원본 크기와 초기 좌표를 기록하여, MouseMove 이벤트에서 부드럽게 크기를 변환할 수 있도록 합니다.
        /// </summary>
        private void Resize_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var el = sender as FrameworkElement;
            if (el != null && el.Tag is string dir)
            {
                _isResizing = true;
                _resizeDir = dir;
                _resizeStartWorldPos = GetWorldPosition(e);

                if (el.DataContext is NodeViewModel nodeVm)
                {
                    _resizingNode = nodeVm;
                    _resizeStartNodeRect = new Rect(nodeVm.X, nodeVm.Y, nodeVm.Width, nodeVm.Height);
                }
                else if (el.DataContext is GroupViewModel groupVm)
                {
                    _resizingGroup = groupVm;
                    _resizeStartGroupRect = new Rect(groupVm.X, groupVm.Y, groupVm.Width, groupVm.Height);
                }

                el.CaptureMouse();
                e.Handled = true;
            }
        }

        /// <summary>
        /// 노드의 상/하/좌/우에 위치한 연결 포트(Port) 동그라미를 마우스로 눌렀을 때 호출됩니다.
        /// 다른 노드나 볼륨으로 이어지는 선(Connector) 긋기 모드를 시작하며, 아직 생성 중(Creating)인 불안정한 객체에서는 선을 그을 수 없도록 차단합니다.
        /// </summary>
        private void Port_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var port = sender as FrameworkElement;
            _sourceItem = port?.DataContext as IConnectableItem;
            if (port?.Tag is string dirStr) Enum.TryParse(dirStr, out _sourceDir);

            if (_sourceItem is NodeViewModel nv && nv.IsCreating)
            {
                _dialogService.ShowMessage("생성 중인 객체는 연결할 수 없습니다.");
                return;
            }

            if (_sourceItem != null)
            {
                _isConnecting = true;
                _startPointCanvas = GetExactBorderPoint(_sourceItem, _sourceDir);
                TempPolyline.Visibility = Visibility.Visible;
                Mouse.Capture(ZoomPanGrid);
                e.Handled = true;
            }
        }

        /// <summary>
        /// 노드의 상단 헤더 영역을 마우스로 클릭했을 때, 해당 노드의 이동(드래그) 모드를 시작합니다.
        /// 마우스 커서와 노드 원점 간의 오프셋을 계산하여 자연스러운 드래그를 준비합니다.
        /// </summary>
        private void Node_Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_isConnecting || _isResizing) return;
            var b = sender as FrameworkElement;
            if (b != null)
            {
                var nodeVm = b.DataContext as NodeViewModel;
                _isNodeDragging = true;
                _draggedNodeElement = b;
                Point mouseWorld = GetWorldPosition(e);
                if (nodeVm != null) _nodeClickOffset = new Point(mouseWorld.X - nodeVm.X, mouseWorld.Y - nodeVm.Y);
                b.CaptureMouse();
                e.Handled = true;
            }
        }

        /// <summary>
        /// 마우스를 드래그하는 동안 노드의 좌표를 실시간으로 갱신하여 화면에 반영합니다.
        /// </summary>
        private void Node_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isNodeDragging && _draggedNodeElement != null)
            {
                var vm = _draggedNodeElement.DataContext as NodeViewModel;
                Point mouseWorld = GetWorldPosition(e);
                if (vm != null) { vm.X = mouseWorld.X - _nodeClickOffset.X; vm.Y = mouseWorld.Y - _nodeClickOffset.Y; }
            }
        }

        /// <summary>
        /// 마우스 버튼을 떼어 노드 드래그를 종료합니다.
        /// (참고: 최종 드롭 위치에 따른 그룹 소속 판별 로직은 최상위 캔버스의 MouseUp 이벤트에서 일괄 처리됩니다.)
        /// </summary>
        private void Node_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isNodeDragging)
            {
                _isNodeDragging = false;
                _draggedNodeElement?.ReleaseMouseCapture();
                _draggedNodeElement = null;
            }
        }

        /// <summary>
        /// 노드의 본문(Body) 영역을 클릭했을 때, 해당 노드를 선택 상태(SelectedElement)로 전환하거나 이미 선택되어 있다면 해제합니다.
        /// 선택된 노드는 우측 사이드바에 상세 정보(Inspect)가 표시됩니다.
        /// </summary>
        private void Node_Body_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_isConnecting || _isResizing) return;
            var b = sender as FrameworkElement;
            var nodeVm = b?.DataContext as NodeViewModel;
            var mainVm = DataContext as MainViewModel;
            if (mainVm != null && nodeVm != null)
            {
                if (mainVm.SelectedElement == nodeVm) mainVm.SelectedElement = null;
                else mainVm.SelectedElement = nodeVm;
            }
            e.Handled = true;
        }

        /// <summary>
        /// 캔버스에 그려진 연결선(Connector) 자체를 클릭했을 때, 선을 선택 상태로 전환합니다.
        /// 선택된 선은 Delete 키를 눌러 삭제할 수 있습니다.
        /// </summary>
        private void Connector_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var el = sender as FrameworkElement;
            var connVm = el?.DataContext as ConnectorViewModel;
            var mainVm = DataContext as MainViewModel;
            if (mainVm != null && connVm != null)
            {
                mainVm.SelectedElement = connVm;
                e.Handled = true;
            }
        }

        // --- 헬퍼 메서드들 ---

        /// <summary>
        /// 특정 객체(노드, 그룹)의 4면(좌, 우, 상, 하) 중 지정된 방향에 위치한 연결 포트의 정확한 절대 좌표(Point)를 계산합니다.
        /// </summary>
        private Point GetExactBorderPoint(IConnectableItem item, PortDirection dir)
        {
            switch (dir)
            {
                case PortDirection.Left: return new Point(item.X, item.CenterY);
                case PortDirection.Right: return new Point(item.X + item.Width, item.CenterY);
                case PortDirection.Top: return new Point(item.CenterX, item.Y);
                case PortDirection.Bottom: return new Point(item.CenterX, item.Y + item.Height);
                default: return new Point(item.CenterX, item.CenterY);
            }
        }

        /// <summary>
        /// WPF 시각적 트리(Visual Tree)를 거슬러 올라가며 조건(Predicate)을 만족하는 특정 타입의 부모 요소를 찾습니다.
        /// 마우스 클릭 이벤트에서 클릭된 UI 조각(HitTest 결과)이 어떤 뷰모델 객체에 속하는지 역추적할 때 사용됩니다.
        /// </summary>
        private T? FindParent<T>(DependencyObject? child, Func<T, bool> predicate) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T t && predicate(t)) return t;
                child = VisualTreeHelper.GetParent(child);
            }
            return null;
        }

        /// <summary>
        /// 사용자의 마우스 좌표가 대상 사각형(Rect)의 4개 면 중 어느 면에 가장 가까운지 수학적으로 계산합니다.
        /// 연결선을 아무렇게나 놓았을 때 가장 자연스러운 포트(상/하/좌/우)를 자동 결정하기 위해 사용됩니다.
        /// </summary>
        private PortDirection GetClosestDirection(Point mouse, Rect rect)
        {
            double distLeft = Math.Abs(mouse.X - rect.Left);
            double distRight = Math.Abs(mouse.X - rect.Right);
            double distTop = Math.Abs(mouse.Y - rect.Top);
            double distBottom = Math.Abs(mouse.Y - rect.Bottom);
            double min = Math.Min(Math.Min(distLeft, distRight), Math.Min(distTop, distBottom));
            if (min == distLeft) return PortDirection.Left;
            if (min == distRight) return PortDirection.Right;
            if (min == distTop) return PortDirection.Top;
            return PortDirection.Bottom;
        }

        /// <summary>
        /// 특정 그룹 안에 들어있는 자식 노드들을 모두 감싸는 최소한의 경계 영역(Bounding Box)을 계산합니다.
        /// 사용자가 그룹 상자를 드래그하여 크기를 줄일 때, 자식 노드들을 침범하며 작아지지 못하게 막는 경계선으로 활용됩니다.
        /// </summary>
        private Rect GetGroupContentBounds(GroupViewModel group)
        {
            if (group.ContainedNodes.Count == 0) return Rect.Empty;

            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;

            foreach (var node in group.ContainedNodes)
            {
                if (node.X < minX) minX = node.X;
                if (node.Y < minY) minY = node.Y;
                if (node.X + node.Width > maxX) maxX = node.X + node.Width;
                if (node.Y + node.Height > maxY) maxY = node.Y + node.Height;
            }

            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        /// <summary>
        /// 현재 작업 중인 파일의 이름에 따라 윈도우 상단의 앱 제목 표시줄 텍스트를 업데이트합니다.
        /// </summary>
        private void UpdateTitle()
        {
            var vm = this.DataContext as MainViewModel;
            if (vm?.CurrentFilePath != null)
            {
                this.Title = $"Visual Docker Manager - {System.IO.Path.GetFileName(vm.CurrentFilePath)}";
            }
            else
            {
                this.Title = "Visual Docker Manager - New File";
            }
        }

        /// <summary>
        /// 시트 탭에서 마우스 우클릭 -> [시트 비우기] 메뉴를 선택했을 때 호출됩니다.
        /// 선택한 시트의 모든 객체를 지우기 전 사용자에게 확인을 받습니다.
        /// </summary>
        private void ClearSheet_Click(object sender, RoutedEventArgs e)
        {
            // 우클릭한 메뉴 아이템에서 데이터(SheetViewModel)를 가져옴
            if (sender is MenuItem menuItem && menuItem.DataContext is SheetViewModel targetSheet)
            {
                // 확인 메시지 띄우기
                bool isConfirmed = _dialogService.ShowConfirm(
                    $"'{targetSheet.Title}' 시트의 모든 내용을 지우시겠습니까?\n(컨테이너, 연결선, 그룹이 모두 삭제됩니다)", "시트 비우기");

                if (isConfirmed)
                {
                    // 해당 시트의 내용물만 싹 비움
                    targetSheet.Nodes.Clear();
                    targetSheet.Connectors.Clear();
                    targetSheet.Groups.Clear();
                }
            }
        }

        /// <summary>
        /// 톱니바퀴(옵션) 팝업 메뉴에서 [이미지 관리] 버튼을 클릭했을 때 호출됩니다.
        /// 사용하지 않는 이미지를 정리하고 검색할 수 있는 전용 팝업 창을 엽니다.
        /// </summary>
        private void ManageImages_Click(object sender, RoutedEventArgs e)
        {
            // 1. 톱니바퀴 드롭다운 팝업 닫기
            OptionPopup.IsOpen = false;

            // 2. 새 창 띄우기 (데이터 공유)
            var imgWindow = new Views.ImageManagerWindow
            {
                DataContext = this.DataContext,
                Owner = this
            };
            imgWindow.ShowDialog();
        }

        /// <summary>
        /// 툴바 상단의 프로필/설정 버튼을 클릭했을 때 호출됩니다.
        /// 원격 서버에 SSH 터널링을 통해 새로운 도커 엔진 탭을 연결하는 다이얼로그를 띄웁니다.
        /// </summary>
        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var sshDlg = new Views.SshConnectionDialog(this.ViewModel, _dialogService);
            sshDlg.Owner = this;
            sshDlg.ShowDialog();
        }
    }
}