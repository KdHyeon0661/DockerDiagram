using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using DockerDiagram.Helpers;

namespace DockerDiagram
{
    public partial class MainWindow : Window
    {
        // --- 1. 기본 변수들 ---
        private Point _toolStartPoint;
        private bool _isToolDragging = false;

        private bool _isNodeDragging = false;
        private Point _nodeClickOffset;
        private FrameworkElement? _draggedNodeElement = null;

        private bool _isConnecting = false;
        private NodeViewModel? _sourceNode = null;
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
        private bool _isGroupingDrag = false;
        private Point _groupStartPoint;
        private Rectangle? _tempGroupRect;

        private bool _isGroupMoving = false;
        private GroupViewModel? _movingGroup = null;
        private Point _groupClickOffset;

        // --- 3. 재연결(Reconnection) 관련 변수 ---
        private bool _isReconnecting = false;
        private ConnectorViewModel? _reconnectingConn = null;
        private string _reconnectType = "";

        private GroupViewModel? _resizingGroup = null;
        private Rect _resizeStartGroupRect;
        private DispatcherTimer _dockerMonitorTimer;
        private DispatcherTimer _autoSaveTimer;

        public MainWindow()
        {
            InitializeComponent();

            _dockerMonitorTimer = new DispatcherTimer();
            _dockerMonitorTimer.Interval = TimeSpan.FromSeconds(5);
            _dockerMonitorTimer.Tick += DockerMonitorTimer_Tick;
            _dockerMonitorTimer.Start();

            _autoSaveTimer = new DispatcherTimer();
            _autoSaveTimer.Interval = TimeSpan.FromMinutes(1);
            _autoSaveTimer.Tick += AutoSaveTimer_Tick!;

            // 3. [기존] 프로그램 켜질 때 자동 로드
            this.Loaded += async (s, e) =>
            {
                string lastFile = Properties.Settings.Default.LastFilePath;

                if (!string.IsNullOrEmpty(lastFile) && System.IO.File.Exists(lastFile))
                {
                    var vm = this.DataContext as MainViewModel;
                    // 자동 로드 (다이얼로그 없이 바로 열기)
                    await Helpers.FileService.LoadDiagramFromPathAsync(vm!, lastFile);
                }
            };
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            string lastFile = Properties.Settings.Default.LastFilePath;
            var vm = this.DataContext as MainViewModel;

            // 경로가 비어있지 않은데
            if (!string.IsNullOrEmpty(lastFile))
            {
                // 실제 파일이 존재하면 -> 로드
                if (System.IO.File.Exists(lastFile))
                {
                    if (vm != null)
                    {
                        await FileService.LoadDiagramFromPathAsync(vm, lastFile);
                        vm.IsModified = false; // 불러온 직후는 변경사항 없음 처리
                    }
                }
                else
                {
                    // 파일이 없다(삭제됨) -> 기억(Setting)을 확실하게 지워버림
                    Properties.Settings.Default.LastFilePath = "";
                    Properties.Settings.Default.Save(); // 즉시 반영

                    // 디버깅용: 확실히 지워졌는지 확인
                    System.Diagnostics.Debug.WriteLine("설정 초기화됨: 파일이 없어 경로를 지웠습니다.");
                }
            }

            _autoSaveTimer.Start();
        }

        // 2. 종료 시 (변경사항 있을 때만 묻기)
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            var vm = this.DataContext as MainViewModel;

            // 변경된 게 없으면(IsModified == false) 묻지 않고 바로 종료
            if (vm != null && !vm.IsModified)
            {
                return; // 그냥 꺼짐
            }

            // 변경사항이 있을 때만 물어봄
            var result = MessageBox.Show("변경 사항이 저장되지 않았습니다.\n저장하고 종료하시겠습니까?",
                                         "종료 확인",
                                         MessageBoxButton.YesNoCancel,
                                         MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true; // 종료 취소
            }
            else if (result == MessageBoxResult.Yes)
            {
                // 저장 로직 (경로 있으면 덮어쓰기, 없으면 다이얼로그)
                string lastFile = Properties.Settings.Default.LastFilePath;
                if (!string.IsNullOrEmpty(lastFile) && System.IO.File.Exists(lastFile))
                {
                    Helpers.FileService.QuickSave(vm!, lastFile);
                }
                else
                {
                    Helpers.FileService.SaveDiagram(vm!);
                    // 만약 저장창에서 취소하면 그냥 꺼지는데, 이를 막으려면 SaveDiagram 반환값 체크 필요
                    // 여기선 일단 진행
                }
            }
        }

        // 자동 저장 로직
        private void AutoSaveTimer_Tick(object sender, EventArgs e)
        {
            string lastFile = Properties.Settings.Default.LastFilePath;
            var vm = this.DataContext as MainViewModel;

            // 저장할 경로가 있고, ViewModel이 유효하면 저장 실행
            if (!string.IsNullOrEmpty(lastFile) && vm != null)
            {
                bool success = FileService.QuickSave(vm, lastFile);
                if (success)
                {
                    System.Diagnostics.Debug.WriteLine($"Auto-saved at {DateTime.Now}");
                }
            }
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateScrollButtonsState();
            var vm = DataContext as MainViewModel;
            if (vm != null && ViewportCanvas.ActualWidth > 0)
            {
                if (vm.MapWidth < 1000) vm.MapWidth = 1000;
                if (vm.MapHeight < 700) vm.MapHeight = 700;
            }

            // 1. 앱 켜질 때 최초 1회 즉시 체크
            await CheckDockerStateAsync();

            // 2. 이후 타이머 시작 (지속 감시)
            _dockerMonitorTimer.Start();
        }

        // 타이머가 째깍거릴 때마다 실행되는 로직
        private async void DockerMonitorTimer_Tick(object? sender, EventArgs e)
        {
            // 도커가 실행 중이면 아무것도 안 함
            if (DockerServiceHelper.IsDockerRunning()) return;

            // 도커가 꺼져있음이 감지됨!

            // 1. 알림창이 중복해서 뜨지 않도록 타이머 잠시 정지
            _dockerMonitorTimer.Stop();

            // 2. 체크 및 복구 시도
            await CheckDockerStateAsync();

            // 3. 상황 종료 후 감시 재개
            _dockerMonitorTimer.Start();
        }

        // 공통 체크 로직 (시작 시 + 감시 중 사용)
        private async Task CheckDockerStateAsync()
        {
            // 도커가 이미 켜져 있다면 패스
            if (DockerServiceHelper.IsDockerRunning()) return;

            // 도커가 꺼져 있으면 물어봄
            var result = MessageBox.Show(
                "Docker 프로세스가 종료되었습니다.\nDocker를 다시 실행하시겠습니까?\n\n('아니요'를 누르면 프로그램이 종료됩니다.)",
                "Docker 감지",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    // 실행 시도
                    await DockerServiceHelper.StartDockerAsync();

                    // (선택) 켜진 후 데이터 자동 갱신
                    // (DataContext as MainViewModel)?.SyncDockerCommand.Execute(null);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Docker 실행 실패: {ex.Message}");
                    // 실행 실패 시에도 종료하고 싶다면 여기에 Application.Current.Shutdown(); 추가
                }
            }
            else
            {
                // '아니요'를 누르면 앱 전체 종료
                Application.Current.Shutdown();
            }
        }

        /*private async Task CheckDockerOnStartup()
        {
            // 도커가 꺼져있는 경우에만 실행
            if (!DockerServiceHelper.IsDockerRunning())
            {
                // 모달창 띄우기 (Yes/No 버튼)
                var result = MessageBox.Show(
                    "Docker가 현재 꺼져있습니다.\nDocker를 실행하시겠습니까?",
                    "Docker 확인",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        // 도커 실행 (비동기 대기)
                        await DockerServiceHelper.StartDockerAsync();

                        // (선택 사항) 도커가 켜진 후 데이터를 바로 불러오고 싶다면 아래 주석 해제
                        // var vm = DataContext as MainViewModel;
                        // if (vm != null) vm.SyncDockerCommand.Execute(null);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Docker 실행 중 오류가 발생했습니다: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            else
            {
                // 도커가 이미 켜져있으면 아무것도 띄우지 않음 (요청하신 대로)

                // (선택 사항) 켜져있을 때 자동으로 데이터를 불러오고 싶다면 아래 주석 해제
                // 단, MainViewModel의 Sync 로직에서 완료 메시지박스를 띄우지 않도록 수정해야 조용히 로드됩니다.
                // (DataContext as MainViewModel)?.SyncDockerCommand.Execute(null);
            }
        }*/

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

                BtnScrollLeft.IsEnabled = !isAtStart;
                BtnScrollRight.IsEnabled = !isAtEnd;
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
            if (e.Key == Key.Delete) (DataContext as MainViewModel)?.DeleteSelected();
        }

        // --- 옵션 팝업 및 기능 버튼 ---
        private void OptionButton_Click(object sender, RoutedEventArgs e)
        {
            MapSizePanel.Visibility = Visibility.Collapsed;
            if (BtnGroupMode != null) BtnGroupMode.IsChecked = _isGroupingMode;
            OptionPopup.IsOpen = true;
        }

        private void BtnGroupMode_Checked(object sender, RoutedEventArgs e)
        {
            _isGroupingMode = true;
            Mouse.OverrideCursor = Cursors.Cross;
            OptionPopup.IsOpen = false;
        }

        private void BtnGroupMode_Unchecked(object sender, RoutedEventArgs e)
        {
            _isGroupingMode = false;
            Mouse.OverrideCursor = null;
        }

        private void BtnAutoLayout_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as MainViewModel;
            vm?.ActiveSheet?.AutoLayout();
            OptionPopup.IsOpen = false;
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
                Mouse.OverrideCursor = _isGroupingMode ? Cursors.Cross : null;
            }
        }

        private void Diagram_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 1. 그룹핑 모드
            if (_isGroupingMode)
            {
                _isGroupingDrag = true;
                _groupStartPoint = GetWorldPosition(e);

                _tempGroupRect = new Rectangle
                {
                    Stroke = Brushes.Gray,
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 4, 2 },
                    RadiusX = 10,
                    RadiusY = 10,
                    Fill = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)),
                    IsHitTestVisible = false
                };

                Canvas.SetLeft(_tempGroupRect, _groupStartPoint.X);
                Canvas.SetTop(_tempGroupRect, _groupStartPoint.Y);
                DragCanvas.Children.Add(_tempGroupRect);

                Mouse.Capture(ViewportCanvas);
                e.Handled = true;
                return;
            }

            // 2. 일반 선택 해제
            if (!e.Handled) (DataContext as MainViewModel)?.ClearSelection();
        }

        private void Diagram_MouseMove(object sender, MouseEventArgs e)
        {
            var vm = DataContext as MainViewModel;
            if (vm?.ActiveSheet == null) return;

            Point current = GetWorldPosition(e);

            // 1. 패닝
            if (_isPanning)
            {
                Point currentMouse = e.GetPosition(this);
                double diffX = currentMouse.X - _panStartClick.X;
                double diffY = currentMouse.Y - _panStartClick.Y;
                vm.ActiveSheet.OffsetX = _panStartTranslate.X + diffX;
                vm.ActiveSheet.OffsetY = _panStartTranslate.Y + diffY;
                return;
            }

            // 2. 그룹 생성 드래그
            if (_isGroupingDrag && _tempGroupRect != null)
            {
                double x = Math.Min(_groupStartPoint.X, current.X);
                double y = Math.Min(_groupStartPoint.Y, current.Y);
                double w = Math.Abs(current.X - _groupStartPoint.X);
                double h = Math.Abs(current.Y - _groupStartPoint.Y);

                Canvas.SetLeft(_tempGroupRect, x);
                Canvas.SetTop(_tempGroupRect, y);
                _tempGroupRect.Width = w;
                _tempGroupRect.Height = h;
                return;
            }

            // 3. 그룹 전체 이동 (10px 스냅 적용)
            if (_isGroupMoving && _movingGroup != null)
            {
                double rawTargetX = current.X - _groupClickOffset.X;
                double rawTargetY = current.Y - _groupClickOffset.Y;

                // 그리드 스냅 (반올림)
                double snappedTargetX = Math.Round(rawTargetX / 10.0) * 10.0;
                double snappedTargetY = Math.Round(rawTargetY / 10.0) * 10.0;

                double dx = snappedTargetX - _movingGroup.X;
                double dy = snappedTargetY - _movingGroup.Y;

                // 불필요한 연산 방지
                if (Math.Abs(dx) >= 1 || Math.Abs(dy) >= 1)
                {
                    _movingGroup.MoveBy(dx, dy);
                }
                return;
            }

            // 4. 리사이징
            if (_isResizing)
            {
                double diffX = current.X - _resizeStartWorldPos.X;
                double diffY = current.Y - _resizeStartWorldPos.Y;

                // A. 노드 리사이징 (기존 유지)
                if (_resizingNode != null)
                {
                    if (_resizeDir.Contains("Right")) _resizingNode.Width = Math.Max(50, _resizeStartNodeRect.Width + diffX);
                    if (_resizeDir.Contains("Bottom")) _resizingNode.Height = Math.Max(50, _resizeStartNodeRect.Height + diffY);
                    if (_resizeDir.Contains("Left")) { double w = _resizeStartNodeRect.Width - diffX; if (w >= 50) { _resizingNode.X = _resizeStartNodeRect.X + diffX; _resizingNode.Width = w; } }
                    if (_resizeDir.Contains("Top")) { double h = _resizeStartNodeRect.Height - diffY; if (h >= 50) { _resizingNode.Y = _resizeStartNodeRect.Y + diffY; _resizingNode.Height = h; } }
                }
                // B. [UPDATED] 그룹 리사이징 (자식 노드 영역 침범 방지)
                else if (_resizingGroup != null)
                {
                    // 1. 자식들이 차지하는 최소 영역(Content Bounds) 계산
                    Rect contentBounds = GetGroupContentBounds(_resizingGroup);
                    double padding = 20; // 여유 공간

                    // 2. 방향별 제한 적용

                    // [오른쪽 핸들]
                    if (_resizeDir.Contains("Right"))
                    {
                        double newWidth = _resizeStartGroupRect.Width + diffX;
                        // 오른쪽 벽이 (자식들의 가장 오른쪽 끝 + 여백)보다 안쪽으로 들어오지 못하게 함
                        double minRequiredWidth = (contentBounds.Right - _resizeStartGroupRect.X) + padding;

                        // 자식이 없으면 기본 50, 있으면 자식 영역 기준
                        double limit = _resizingGroup.ContainedNodes.Count > 0 ? minRequiredWidth : 50;
                        _resizingGroup.Width = Math.Max(limit, newWidth);
                    }

                    // [아래쪽 핸들]
                    if (_resizeDir.Contains("Bottom"))
                    {
                        double newHeight = _resizeStartGroupRect.Height + diffY;
                        double minRequiredHeight = (contentBounds.Bottom - _resizeStartGroupRect.Y) + padding;

                        double limit = _resizingGroup.ContainedNodes.Count > 0 ? minRequiredHeight : 50;
                        _resizingGroup.Height = Math.Max(limit, newHeight);
                    }

                    // [왼쪽 핸들] (X좌표와 Width가 동시에 변함)
                    if (_resizeDir.Contains("Left"))
                    {
                        double rawNewX = _resizeStartGroupRect.X + diffX;

                        // 왼쪽 벽이 (자식들의 가장 왼쪽 끝 - 여백)보다 오른쪽으로 가지 못하게 함
                        double maxAllowedX = _resizingGroup.ContainedNodes.Count > 0
                                             ? contentBounds.Left - padding
                                             : _resizeStartGroupRect.Right - 50;

                        double constrainedX = Math.Min(rawNewX, maxAllowedX);
                        double newWidth = _resizeStartGroupRect.Right - constrainedX; // 우측 고정, 좌측 이동

                        _resizingGroup.X = constrainedX;
                        _resizingGroup.Width = newWidth;
                    }

                    // [위쪽 핸들] (Y좌표와 Height가 동시에 변함)
                    if (_resizeDir.Contains("Top"))
                    {
                        double rawNewY = _resizeStartGroupRect.Y + diffY;

                        double maxAllowedY = _resizingGroup.ContainedNodes.Count > 0
                                             ? contentBounds.Top - padding
                                             : _resizeStartGroupRect.Bottom - 50;

                        double constrainedY = Math.Min(rawNewY, maxAllowedY);
                        double newHeight = _resizeStartGroupRect.Bottom - constrainedY; // 바닥 고정, 천장 이동

                        _resizingGroup.Y = constrainedY;
                        _resizingGroup.Height = newHeight;
                    }
                }
                return;
            }

            // 5. 재연결 (Grip Drag)
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
                    r1 = new Rect(current.X, current.Y, 0, 0);
                    r2 = new Rect(_reconnectingConn.Target.X, _reconnectingConn.Target.Y, _reconnectingConn.Target.Width, _reconnectingConn.Target.Height);
                }
                else
                {
                    startP = _reconnectingConn.SourcePos;
                    endP = current;
                    d1 = _reconnectingConn.SourceDir;
                    d2 = PortDirection.None;
                    r1 = new Rect(_reconnectingConn.Source.X, _reconnectingConn.Source.Y, _reconnectingConn.Source.Width, _reconnectingConn.Source.Height);
                    r2 = new Rect(current.X, current.Y, 0, 0);
                }

                // 자석 효과
                var hitResult = VisualTreeHelper.HitTest(ZoomPanGrid, e.GetPosition(ZoomPanGrid));
                if (hitResult != null)
                {
                    var hitNodeObj = FindParent<FrameworkElement>(hitResult.VisualHit, el => el.DataContext is NodeViewModel);
                    if (hitNodeObj != null && hitNodeObj.DataContext is NodeViewModel hoverNode)
                    {
                        Rect nodeRect = new Rect(hoverNode.X, hoverNode.Y, hoverNode.Width, hoverNode.Height);
                        PortDirection hoverDir = GetClosestDirection(current, nodeRect);
                        Point hoverPoint = GetExactBorderPoint(hoverNode, hoverDir);

                        if (_reconnectType == "Source") { startP = hoverPoint; d1 = hoverDir; r1 = nodeRect; }
                        else { endP = hoverPoint; d2 = hoverDir; r2 = nodeRect; }
                    }
                }
                TempPolyline.Points = OrthogonalRouter.GetRoute(startP, d1, endP, d2, r1, r2);
                return;
            }

            // 6. 신규 연결
            if (_isConnecting && _sourceNode != null)
            {
                Point startP = GetExactBorderPoint(_sourceNode, _sourceDir);
                PortDirection targetDir = PortDirection.None;
                Rect sourceRect = new Rect(_sourceNode.X, _sourceNode.Y, _sourceNode.Width, _sourceNode.Height);
                Rect targetRect = new Rect(current.X, current.Y, 0, 0);
                Point endPoint = current;

                if (_lastHitPort != null) { _lastHitPort.Background = Brushes.Transparent; _lastHitPort = null; }

                var hitResult = VisualTreeHelper.HitTest(ZoomPanGrid, e.GetPosition(ZoomPanGrid));
                if (hitResult != null)
                {
                    var hitNodeObj = FindParent<FrameworkElement>(hitResult.VisualHit, el => el.DataContext is NodeViewModel);
                    if (hitNodeObj != null && hitNodeObj.DataContext is NodeViewModel targetNode && targetNode != _sourceNode)
                    {
                        targetRect = new Rect(targetNode.X, targetNode.Y, targetNode.Width, targetNode.Height);
                        targetDir = GetClosestDirection(current, targetRect);
                        endPoint = GetExactBorderPoint(targetNode, targetDir);
                    }
                }
                TempPolyline.Points = OrthogonalRouter.GetRoute(startP, _sourceDir, endPoint, targetDir, sourceRect, targetRect);
            }
        }

        protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonUp(e);
            _isClickedOnTab = false;

            // 1. 그룹핑 생성 완료 (드래그 종료)
            if (_isGroupingDrag && _tempGroupRect != null)
            {
                _isGroupingDrag = false;
                Mouse.Capture(null);

                double w = _tempGroupRect.Width;
                double h = _tempGroupRect.Height;

                // 너무 작은 박스는 생성하지 않음 (오클릭 방지)
                if (w > 20 && h > 20)
                {
                    double x = Canvas.GetLeft(_tempGroupRect);
                    double y = Canvas.GetTop(_tempGroupRect);

                    var vm = DataContext as MainViewModel;
                    if (vm?.ActiveSheet != null)
                    {
                        var newGroup = new GroupViewModel(x, y, w, h);

                        // ★★★ [핵심 수정] Start All 기능을 위해 부모 시트를 반드시 연결해야 함 ★★★
                        newGroup.ParentSheet = vm.ActiveSheet;

                        // 시트에 그룹 추가
                        vm.ActiveSheet.Groups.Add(newGroup);

                        // 생성된 그룹 박스 안에 이미 노드가 있다면 자동으로 포함시킴
                        vm.ActiveSheet.RefreshGroupContainment(newGroup);

                        // 생성 후 즉시 선택 상태로 변경
                        vm.SelectedElement = newGroup;
                    }
                }

                // 임시 사각형 제거 및 초기화
                DragCanvas.Children.Remove(_tempGroupRect);
                _tempGroupRect = null;
                _isGroupingMode = false;
                if (BtnGroupMode != null) BtnGroupMode.IsChecked = false;
                Mouse.OverrideCursor = null;
                return;
            }

            // 2. 그룹 이동 종료
            if (_isGroupMoving)
            {
                _isGroupMoving = false;
                Mouse.Capture(null);
                _movingGroup = null;
            }

            // 3. 노드 드래그 종료 -> 그룹 합류/이탈 판정
            if (_isNodeDragging && _draggedNodeElement != null)
            {
                var nodeVm = _draggedNodeElement.DataContext as NodeViewModel;
                var sheet = (DataContext as MainViewModel)?.ActiveSheet;

                if (nodeVm != null && sheet != null)
                {
                    // 드래그가 끝난 위치에 그룹이 있는지 확인
                    var targetGroup = sheet.FindGroupAt(nodeVm.X, nodeVm.Y, nodeVm.Width, nodeVm.Height);

                    foreach (var group in sheet.Groups)
                    {
                        if (group == targetGroup) group.AddNode(nodeVm); // 해당 그룹에 추가
                        else group.RemoveNode(nodeVm); // 다른 그룹에서는 제거
                    }
                }

                // 드래그 상태 해제
                _isNodeDragging = false;
                _draggedNodeElement?.ReleaseMouseCapture();
                _draggedNodeElement = null;
            }

            // 4. 재연결(Reconnection) 완료
            if (_isReconnecting && _reconnectingConn != null)
            {
                _isReconnecting = false;
                var el = Mouse.Captured as FrameworkElement;
                el?.ReleaseMouseCapture();
                TempPolyline.Visibility = Visibility.Collapsed;

                var hitResult = VisualTreeHelper.HitTest(ZoomPanGrid, e.GetPosition(ZoomPanGrid));
                if (hitResult != null)
                {
                    var hitNodeObj = FindParent<FrameworkElement>(hitResult.VisualHit, x => x.DataContext is NodeViewModel);
                    if (hitNodeObj != null && hitNodeObj.DataContext is NodeViewModel hitNode)
                    {
                        Rect nodeRect = new Rect(hitNode.X, hitNode.Y, hitNode.Width, hitNode.Height);
                        PortDirection newDir = GetClosestDirection(GetWorldPosition(e), nodeRect);

                        if (_reconnectType == "Source")
                            _reconnectingConn.UpdateConnection(hitNode, newDir, _reconnectingConn.Target, _reconnectingConn.TargetDir);
                        else
                            _reconnectingConn.UpdateConnection(_reconnectingConn.Source, _reconnectingConn.SourceDir, hitNode, newDir);
                    }
                }
                _reconnectingConn = null;
                return;
            }

            // 5. 리사이징 종료
            if (_isResizing)
            {
                _isResizing = false;
                var el = Mouse.Captured as FrameworkElement;
                el?.ReleaseMouseCapture();
                _resizingNode = null;
                _resizingGroup = null;
            }

            // 6. 신규 연결(Connection) 종료
            if (_isConnecting)
            {
                _isConnecting = false;
                Mouse.Capture(null);
                TempPolyline.Visibility = Visibility.Collapsed;
                if (_lastHitPort != null) { _lastHitPort.Background = Brushes.Transparent; _lastHitPort = null; }

                var hitResult = VisualTreeHelper.HitTest(ZoomPanGrid, e.GetPosition(ZoomPanGrid));
                if (hitResult != null)
                {
                    var hitNodeObj = FindParent<FrameworkElement>(hitResult.VisualHit, el => el.DataContext is NodeViewModel);
                    if (hitNodeObj != null && hitNodeObj.DataContext is NodeViewModel targetNode)
                    {
                        // 생성 중인 노드에는 연결 불가
                        if (targetNode.IsCreating)
                        {
                            MessageBox.Show("생성 중인 객체에는 연결할 수 없습니다.", "연결 불가", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                        // 자기 자신에게 연결 불가
                        else if (targetNode != _sourceNode && _sourceNode != null)
                        {
                            Rect targetRect = new Rect(targetNode.X, targetNode.Y, targetNode.Width, targetNode.Height);
                            PortDirection targetDir = GetClosestDirection(GetWorldPosition(e), targetRect);
                            (DataContext as MainViewModel)?.AddConnection(_sourceNode, targetNode, _sourceDir, targetDir);
                        }
                    }
                }
                _sourceNode = null;
            }
        }

        private void GroupHeader_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement border) return;
            _movingGroup = border.DataContext as GroupViewModel;

            if (_movingGroup != null)
            {
                _isGroupMoving = true;
                Point mouseWorld = GetWorldPosition(e);
                _groupClickOffset = new Point(mouseWorld.X - _movingGroup.X, mouseWorld.Y - _movingGroup.Y);

                // Note: 여기서 SelectedElement 설정 코드가 삭제되었습니다. 오직 이동만 합니다.

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

            // Tag가 string인지 확인하고 안전하게 할당 (null일 경우 else 블록)
            if (ellipse.Tag is string tag)
            {
                _reconnectType = tag;
            }
            else
            {
                _reconnectType = string.Empty;
            }

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

        private void Tool_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _toolStartPoint = e.GetPosition(null);
            _isToolDragging = false;
        }

        private void Tool_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isToolDragging)
            {
                if (Math.Abs(e.GetPosition(null).X - _toolStartPoint.X) > 5)
                {
                    _isToolDragging = true;

                    // 드래그된 요소의 Tag 확인 (Container, Volume, Network)
                    var border = sender as Border;
                    string typeStr = border?.Tag?.ToString() ?? "Container";
                    NodeType type = (NodeType)Enum.Parse(typeof(NodeType), typeStr);

                    // 타입에 따른 기본값 설정
                    var c = new DockerContainer { Type = type };
                    if (type == NodeType.Container) { c.Name = "New Container"; c.Image = "nginx:latest"; }
                    else if (type == NodeType.Volume) { c.Name = "New Volume"; c.Image = "local"; }
                    else if (type == NodeType.Network) { c.Name = "New Net"; c.Image = "bridge"; }

                    DragDrop.DoDragDrop((DependencyObject)sender, new DataObject("DockerContainerObject", c), DragDropEffects.Copy);
                }
            }
        }

        private async void Tool_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // 드래그 중이 아니었다면 (즉, 단순 클릭이었다면)
            if (!_isToolDragging)
            {
                var border = sender as Border;
                string typeStr = border?.Tag?.ToString() ?? "Container";

                // Tag가 "Container", "Volume", "Network" 중 하나인지 확인 후 변환
                if (Enum.TryParse(typeStr, out NodeType type))
                {
                    var vm = DataContext as MainViewModel;
                    if (vm != null)
                    {
                        // 생성 위치는 화면 중앙 부근(200, 200)으로 고정
                        double defaultX = 200;
                        double defaultY = 200;

                        // [CASE 1] 컨테이너 버튼 클릭
                        if (type == NodeType.Container)
                        {
                            var dlg = new DockerDiagram.Views.ContainerDialog();
                            dlg.Owner = this;
                            if (dlg.ShowDialog() == true)
                            {
                                await vm.CreateNewContainerNodeAsync(
                                    dlg.ContainerName,
                                    dlg.ImageName,
                                    "latest",
                                    dlg.Ports,
                                    dlg.EnvVars,
                                    dlg.Volumes,
                                    dlg.RestartPolicy,
                                    dlg.MemoryMb,
                                    dlg.CpuCount,
                                    defaultX, defaultY);
                            }
                        }
                        // [CASE 2] 볼륨 버튼 클릭
                        else if (type == NodeType.Volume)
                        {
                            var dlg = new DockerDiagram.Views.VolumeDialog();
                            dlg.Owner = this;
                            if (dlg.ShowDialog() == true)
                            {
                                await vm.CreateNewVolumeNodeAsync(dlg.VolumeName, dlg.Driver, defaultX, defaultY);
                            }
                        }
                        // [CASE 3] 네트워크 버튼 클릭
                        else if (type == NodeType.Network)
                        {
                            var dlg = new DockerDiagram.Views.NetworkDialog();
                            dlg.Owner = this;
                            if (dlg.ShowDialog() == true)
                            {
                                await vm.CreateNewNetworkNodeAsync(dlg.NetworkName, dlg.Driver, defaultX, defaultY);
                            }
                        }
                    }
                }
            }
        }

        private async void Canvas_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("DockerContainerObject")) return;
            var d = e.Data.GetData("DockerContainerObject") as DockerContainer;
            var vm = DataContext as MainViewModel;
            if (d == null || vm == null || vm.ActiveSheet is not SheetViewModel sheet) return;

            // 좌표 계산
            Point mouseOnScreen = e.GetPosition((UIElement)ZoomPanGrid.Parent);
            Point worldPos = ZoomPanGrid.RenderTransform.Inverse.Transform(mouseOnScreen);
            double snapX = Math.Round((worldPos.X - 80) / 10) * 10;
            double snapY = Math.Round((worldPos.Y - 40) / 10) * 10;

            // [CASE A] ID가 없음 = 템플릿 드래그 (단, 볼륨은 예외 처리 필요)
            if (string.IsNullOrEmpty(d.Id))
            {
                var api = new DockerDiagram.Helpers.DockerApiService();

                // 1. 컨테이너 템플릿
                if (d.Type == NodeType.Container)
                {
                    var dlg = new DockerDiagram.Views.ContainerDialog();
                    dlg.Owner = this;
                    if (d.Image != "New Container") dlg.ImageName = d.Image;

                    if (dlg.ShowDialog() == true)
                    {
                        try
                        {
                            Mouse.OverrideCursor = Cursors.Wait;
                            string fullImage = dlg.ImageName.Contains(":") ? dlg.ImageName : dlg.ImageName + ":latest";
                            var parts = fullImage.Split(new[] { ':' }, 2);

                            string newId = await api.CreateAndStartContainerAsync(
                                dlg.ContainerName, parts[0], parts[1], dlg.Ports, dlg.EnvVars, dlg.Volumes, dlg.RestartPolicy, dlg.MemoryMb, dlg.CpuCount);

                            // ID 12자리로 자르기
                            if (newId.Length > 12) newId = newId.Substring(0, 12);

                            await vm.CreateNodeAtAsync(new DockerContainer { Id = newId, Name = dlg.ContainerName, Image = dlg.ImageName, Type = NodeType.Container, State = "running", StateColor = "#28a745" }, snapX, snapY);
                        }
                        catch (Exception ex) { MessageBox.Show($"Error: {ex.Message}"); }
                        finally { Mouse.OverrideCursor = null; }
                    }
                }
                // 2. 볼륨 템플릿
                else if (d.Type == NodeType.Volume)
                {
                    // 기존 볼륨은 ID가 비어있어도 이름이 다르므로 else로 넘어갑니다.

                    if (d.Name == "New Volume")
                    {
                        var dlg = new DockerDiagram.Views.VolumeDialog();
                        dlg.Owner = this;
                        if (dlg.ShowDialog() == true)
                        {
                            try
                            {
                                
                                await api.CreateVolumeAsync(dlg.VolumeName, dlg.Driver);
                                vm.ActiveSheet.CreateNodeAt(new DockerContainer { Name = dlg.VolumeName, Type = NodeType.Volume, Image = dlg.Driver }, snapX, snapY);
                            }
                            catch (Exception ex) { MessageBox.Show($"Error: {ex.Message}"); }
                        }
                    }
                    else
                    {
                        // "New Volume"이 아니면 기존 볼륨을 드래그한 것이므로 바로 배치!
                        await vm.CreateNodeAtAsync(d, snapX, snapY);
                    }
                }
                // 3. 네트워크 템플릿
                else if (d.Type == NodeType.Network)
                {
                    var dlg = new DockerDiagram.Views.NetworkDialog();
                    dlg.Owner = this;
                    if (dlg.ShowDialog() == true)
                    {
                        try
                        {
                            string netId = await api.CreateNetworkAsync(dlg.NetworkName, dlg.Driver);
                            // 네트워크 ID 12자리로 자르기
                            if (netId.Length > 12) netId = netId.Substring(0, 12);

                            vm.ActiveSheet.CreateNodeAt(new DockerContainer { Id = netId, Name = dlg.NetworkName, Type = NodeType.Network, Image = dlg.Driver }, snapX, snapY);
                        }
                        catch (Exception ex) { MessageBox.Show($"Error: {ex.Message}"); }
                    }
                }
            }
            // [CASE B] ID가 있음 (기존 컨테이너/네트워크)
            else
            {
                if (string.Equals(d.State, "running", StringComparison.OrdinalIgnoreCase))
                {
                    d.StateColor = "#28a745"; // 초록색 (Running)
                }
                else if (string.Equals(d.State, "exited", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(d.State, "dead", StringComparison.OrdinalIgnoreCase))
                {
                    d.StateColor = "#dc3545"; // 붉은색 (Stopped)
                }
                else
                {
                    d.StateColor = "#808080"; // 그 외(Created, Paused 등)는 회색
                }

                await vm.CreateNodeAtAsync(d, snapX, snapY);
            }
        }

        private void Resize_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var el = sender as FrameworkElement;
            if (el != null && el.Tag is string dir)
            {
                _isResizing = true;
                _resizeDir = dir;
                _resizeStartWorldPos = GetWorldPosition(e);

                // 노드 리사이징인 경우
                if (el.DataContext is NodeViewModel nodeVm)
                {
                    _resizingNode = nodeVm;
                    _resizeStartNodeRect = new Rect(nodeVm.X, nodeVm.Y, nodeVm.Width, nodeVm.Height);
                }
                // 그룹 리사이징인 경우
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
            _sourceNode = port?.DataContext as NodeViewModel;
            if (port?.Tag is string dirStr) Enum.TryParse(dirStr, out _sourceDir);

            // 생성 중(IsCreating)인 노드는 연결 시작 금지
            if (_sourceNode != null && _sourceNode.IsCreating)
            {
                MessageBox.Show("생성 중인 객체는 연결할 수 없습니다.", "연결 불가", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_sourceNode != null)
            {
                _isConnecting = true;
                _startPointCanvas = GetExactBorderPoint(_sourceNode, _sourceDir);
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

        private Point GetExactBorderPoint(NodeViewModel node, PortDirection dir)
        {
            switch (dir)
            {
                case PortDirection.Left: return new Point(node.X, node.CenterY);
                case PortDirection.Right: return new Point(node.X + node.Width, node.CenterY);
                case PortDirection.Top: return new Point(node.CenterX, node.Y);
                case PortDirection.Bottom: return new Point(node.CenterX, node.Y + node.Height);
                default: return new Point(node.CenterX, node.CenterY);
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

        private void TemplateItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _toolStartPoint = e.GetPosition(null);
            _isToolDragging = false;
        }

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
                        // 템플릿을 DockerContainer 객체로 변환하여 드래그 시작
                        var container = new DockerContainer
                        {
                            Name = template.Name, // 템플릿 이름 사용
                            Image = template.Image,
                            Type = template.Type
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

        private void ExistingItem_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isToolDragging)
            {
                if (Math.Abs(e.GetPosition(null).X - _toolStartPoint.X) > 5)
                {
                    _isToolDragging = true;
                    var border = sender as Border;
                    if (border != null && border.DataContext is DockerContainer container)
                    {
                        DataObject data = new DataObject("DockerContainerObject", container);
                        DragDrop.DoDragDrop(border, data, DragDropEffects.Copy);
                        _isToolDragging = false;
                    }
                }
            }
        }
    }
}