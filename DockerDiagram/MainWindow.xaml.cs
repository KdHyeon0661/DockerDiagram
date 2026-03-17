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
    [SupportedOSPlatform("windows")]
    public partial class MainWindow : Window
    {
        // --- 1. 기본 변수들 ---
        private Point _toolStartPoint;
        private bool _isToolDragging = false;

        private Point _startPoint;

        private bool _isNodeDragging = false;
        private Point _nodeClickOffset;
        private FrameworkElement? _draggedNodeElement = null;

        private bool _isConnecting = false;
        private IConnectableItem? _sourceItem = null;
        private Point _startPointCanvas;
        private Border? _lastHitPort = null;
        private PortDirection _sourceDir = PortDirection.None;

        private bool _isResizing = false;
        private string _resizeDir = "";
        private NodeViewModel? _resizingNode = null;
        private Point _resizeStartWorldPos;
        private Rect _resizeStartNodeRect;

        private bool _isPanning = false;
        private Point _panStartClick;
        private Point _panStartTranslate;

        private Point _sheetDragStartPoint;
        private bool _isSheetDragging = false;
        private bool _isClickedOnTab = false;
        private SheetViewModel? _renamingSheet;

        private const double SCROLL_OFFSET = 400.0;

        // --- 2. 그룹핑(Grouping) 관련 변수 ---
        private bool _isGroupingMode = false;

        private bool _isGroupMoving = false;
        private GroupViewModel? _movingGroup = null;
        private Point _groupClickOffset;

        private bool _isNetworkDrawingMode = false;

        // --- 3. 재연결(Reconnection) 관련 변수 ---
        private bool _isReconnecting = false;
        private ConnectorViewModel? _reconnectingConn = null;
        private string _reconnectType = "";

        private GroupViewModel? _resizingGroup = null;
        private Rect _resizeStartGroupRect;

        private DispatcherTimer _dockerMonitorTimer;
        private DispatcherTimer _autoSaveTimer;

        private readonly IContainerService _containerService;
        private readonly IVolumeService _volumeService;
        private readonly INetworkService _networkService;
        private readonly ISystemService _systemService;
        private readonly IDialogService _dialogService;

        // 생성자
        public MainWindow(MainViewModel viewModel, IDockerService dockerService, IDialogService dialogService)
        {
            InitializeComponent();

            // 1. 도커 서비스 및 다이얼로그 서비스 검증 및 할당
            if (dockerService == null) throw new ArgumentNullException(nameof(dockerService));

            _containerService = dockerService;
            _volumeService = dockerService;
            _networkService = dockerService;
            _systemService = dockerService;

            // 님께서 강조하신 Null 체크 구문 유지
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

        // 2. 종료 시 (변경사항 있을 때만 묻기)
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
                    // DialogService 사용을 권장하나 기존 로직 유지
                    string? savedPath = FileService.SaveDiagramAs(vm, _dialogService);
                    if (string.IsNullOrEmpty(savedPath))
                    {
                        e.Cancel = true;
                    }
                }
            }
        }

        // 자동 저장 로직
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

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateScrollButtonsState();
            var vm = this.DataContext as MainViewModel;
            if (vm == null) return;

            // 1. 마지막 파일 불러오기 로직 (기존 유지)
            string lastFile = Properties.Settings.Default.LastFilePath;
            if (!string.IsNullOrEmpty(lastFile) && System.IO.File.Exists(lastFile))
            {
                await FileService.LoadDiagramFromPathAsync(vm, lastFile, _containerService, _volumeService, _networkService, _dialogService);
                vm.CurrentFilePath = lastFile;
                vm.IsModified = false;
                vm.ActiveSheet?.UpdateGroupLayering();
            }
            else
            {
                vm.CurrentFilePath = null;
            }

            UpdateTitle();

            await CheckDockerStateAsync();

            // 2. 타이머 시작
            if (!_dockerMonitorTimer.IsEnabled) _dockerMonitorTimer.Start();

            // 자동 저장 타이머 시작
            if (!_autoSaveTimer.IsEnabled) _autoSaveTimer.Start();
        }

        private async void DockerMonitorTimer_Tick(object? sender, EventArgs e)
        {
            if (DockerServiceHelper.IsDockerRunning()) return;

            _dockerMonitorTimer.Stop();
            await CheckDockerStateAsync();
            _dockerMonitorTimer.Start();
        }

        private async Task CheckDockerStateAsync()
        {
            if (DockerServiceHelper.IsDockerRunning()) return;

            bool result = _dialogService.ShowConfirm(
                "Docker 프로세스가 종료되었습니다.\nDocker를 다시 실행하시겠습니까?\n\n('아니요'를 누르면 프로그램이 종료됩니다.)",
                "Docker 감지");

            if (result)
            {
                try
                {
                    await DockerServiceHelper.StartDockerAsync(_systemService, _dialogService);

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
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                (DataContext as MainViewModel)?.DeleteCommand.Execute(null);
                e.Handled = true;
            }
        }

        // --- 옵션 팝업 및 기능 버튼 ---
        private void OptionButton_Click(object sender, RoutedEventArgs e)
        {
            MapSizePanel.Visibility = Visibility.Collapsed;
            OptionPopup.IsOpen = true;
        }

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

        private void CloseMapSize_Click(object sender, RoutedEventArgs e) => MapSizePanel.Visibility = Visibility.Collapsed;

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

        private void Diagram_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var vm = DataContext as MainViewModel;
            if (vm?.ActiveSheet == null) return;
            e.Handled = true;
            double zoomFactor = e.Delta > 0 ? 1.1 : 0.9;
            vm.ActiveSheet.Scale *= zoomFactor;
        }

        private void Diagram_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isPanning = true;
            _panStartClick = e.GetPosition(this);
            _panStartTranslate = new Point(MapTranslate.X, MapTranslate.Y);
            Mouse.OverrideCursor = Cursors.SizeAll;
            ZoomPanGrid.CaptureMouse();
        }

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
                        if (_isGroupingMode)
                        {
                            var newGroup = new GroupViewModel(x, y, w, h, null, _dialogService);
                            newGroup.ParentSheet = vm.ActiveSheet;
                            vm.ActiveSheet.Groups.Add(newGroup);
                            vm.ActiveSheet.RefreshGroupContainment(newGroup);
                            vm.SelectedElement = newGroup;

                            vm.ActiveSheet.UpdateGroupLayering();
                        }
                        // B) 네트워크 생성
                        else if (_isNetworkDrawingMode)
                        {
                            var dlg = new Views.NetworkDialog();
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

        private void SheetTab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _sheetDragStartPoint = e.GetPosition(null);
            _isSheetDragging = false;
            _isClickedOnTab = true;
        }

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            base.OnPreviewMouseMove(e);
            if (e.LeftButton == MouseButtonState.Pressed && !_isSheetDragging && _isClickedOnTab)
            {
                var point = e.GetPosition(null);
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

        private void RenameOK_Click(object sender, RoutedEventArgs e)
        {
            if (_renamingSheet != null && !string.IsNullOrWhiteSpace(txtRename.Text))
                (DataContext as MainViewModel)?.RenameSheet(_renamingSheet, txtRename.Text.Trim());
            RenameOverlay.Visibility = Visibility.Collapsed;
            _renamingSheet = null;
        }

        private void RenameCancel_Click(object sender, RoutedEventArgs e)
        {
            RenameOverlay.Visibility = Visibility.Collapsed;
            _renamingSheet = null;
        }

        private void DeleteSheet_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var contextMenu = menuItem?.Parent as ContextMenu;
            var border = contextMenu?.PlacementTarget as FrameworkElement;
            if (border?.DataContext is SheetViewModel sheet)
                (DataContext as MainViewModel)?.DeleteSheet(sheet);
        }

        private void SheetMenuButton_Click(object sender, RoutedEventArgs e) => SheetMenuPopup.IsOpen = true;
        private void SheetMenuList_SelectionChanged(object sender, SelectionChangedEventArgs e) => SheetMenuPopup.IsOpen = false;
        private void SheetListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SheetListBox.SelectedItem != null) SheetListBox.ScrollIntoView(SheetListBox.SelectedItem);
        }

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
                            var dlg = new Views.ContainerDialog();
                            dlg.Owner = this;
                            if (dlg.ShowDialog() == true)
                            {
                                await vm.CreateNewContainerNodeAsync(
                                    dlg.ContainerName, dlg.ImageName, "latest", dlg.Ports, dlg.EnvVars, dlg.Volumes, dlg.RestartPolicy, dlg.MemoryMb, dlg.CpuCount, defaultX, defaultY);
                            }
                        }
                        else if (type == NodeType.Volume)
                        {
                            var dlg = new Views.VolumeDialog();
                            dlg.Owner = this;
                            if (dlg.ShowDialog() == true)
                            {
                                await vm.CreateNewVolumeNodeAsync(dlg.VolumeName, dlg.Driver, defaultX, defaultY);
                            }
                        }
                        else if (type == NodeType.Internet)
                        {
                            vm.ActiveSheet.CreateInternetAt(new DockerInternet { Name = "Internet" }, defaultX, defaultY);
                        }
                    }
                }
            }
        }

        // 캔버스 드롭 핸들러 (네트워크 삭제 반영)
        private async void Canvas_Drop(object sender, DragEventArgs e)
        {
            var vm = DataContext as MainViewModel;
            if (vm?.ActiveSheet == null) return;

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

                if (string.IsNullOrEmpty(d.Id)) // "New" 아이템
                {
                    if (d is DockerContainer container)
                    {
                        var dlg = new Views.ContainerDialog();
                        dlg.Owner = this;
                        if (container.Image != "New Container") dlg.ImageName = container.Image;

                        if (dlg.ShowDialog() == true)
                        {
                            try
                            {
                                Mouse.OverrideCursor = Cursors.Wait;
                                string fullImage = dlg.ImageName.Contains(":") ? dlg.ImageName : dlg.ImageName + ":latest";
                                var parts = fullImage.Split(new[] { ':' }, 2);

                                await vm.CreateNewContainerNodeAsync(
                                    dlg.ContainerName, parts[0], parts.Length > 1 ? parts[1] : "latest",
                                    dlg.Ports, dlg.EnvVars, dlg.Volumes, dlg.RestartPolicy, dlg.MemoryMb, dlg.CpuCount, snapX, snapY);
                            }
                            catch (Exception ex)
                            {
                                _dialogService.ShowMessage($"에러 : {ex.Message}");
                            }
                            finally { Mouse.OverrideCursor = null; }
                        }
                    }
                    else if (d is DockerVolume volume)
                    {
                        if (volume.Name == "New Volume")
                        {
                            var dlg = new Views.VolumeDialog();
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
                        vm.ActiveSheet.CreateInternetAt(new DockerInternet { Name = "Internet" }, snapX, snapY);
                    }
                }
                else // 기존 아이템
                {
                    if (d is DockerContainer existingContainer)
                    {
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
                var group = e.Data.GetData("DockerGroupObject") as DockerGroup;
                if (group != null)
                {
                    await vm.CreateNodeAtAsync(group, snapX, snapY);
                    needsLayerUpdate = true; // ★ [체크] 그룹이 추가되었으니 레이어 정리 필요
                }
            }

            if (needsLayerUpdate)
            {
                vm.ActiveSheet.UpdateGroupLayering();
            }
        }

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
                    else if (border?.DataContext is DockerGroup group)
                    {
                        DataObject data = new DataObject("DockerGroupObject", group);
                        DragDrop.DoDragDrop(border, data, DragDropEffects.Copy);
                    }

                    _isToolDragging = false;
                }
            }
        }

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

        private void ExistingItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _toolStartPoint = e.GetPosition(null);
            _isToolDragging = false;
        }

        private void TemplateItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _toolStartPoint = e.GetPosition(null);
            _isToolDragging = false;
        }

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

        private void Node_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isNodeDragging && _draggedNodeElement != null)
            {
                var vm = _draggedNodeElement.DataContext as NodeViewModel;
                Point mouseWorld = GetWorldPosition(e);
                if (vm != null) { vm.X = mouseWorld.X - _nodeClickOffset.X; vm.Y = mouseWorld.Y - _nodeClickOffset.Y; }
            }
        }

        private void Node_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isNodeDragging)
            {
                _isNodeDragging = false;
                _draggedNodeElement?.ReleaseMouseCapture();
                _draggedNodeElement = null;
            }
        }

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

        private T? FindParent<T>(DependencyObject? child, Func<T, bool> predicate) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T t && predicate(t)) return t;
                child = VisualTreeHelper.GetParent(child);
            }
            return null;
        }

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

        private void ClearSheet_Click(object sender, RoutedEventArgs e)
        {
            // 우클릭한 메뉴 아이템에서 데이터(SheetViewModel)를 가져옴
            if (sender is MenuItem menuItem && menuItem.DataContext is SheetViewModel targetSheet)
            {
                // 확인 메시지 띄우기
                var result = MessageBox.Show(
                    $"'{targetSheet.Title}' 시트의 모든 내용을 지우시겠습니까?\n(컨테이너, 연결선, 그룹이 모두 삭제됩니다)",
                    "시트 비우기",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    // 해당 시트의 내용물만 싹 비움
                    targetSheet.Nodes.Clear();
                    targetSheet.Connectors.Clear();
                    targetSheet.Groups.Clear();
                }
            }
        }
    }
}