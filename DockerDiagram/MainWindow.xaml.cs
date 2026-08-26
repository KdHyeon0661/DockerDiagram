using DockerDiagram.Infrastructure;
using DockerDiagram.Diagram;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Contracts;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Diagnostics;
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
        private bool _isStackTemplateDragging = false;
        private bool _isComposeProjectDragging = false;

        // 캔버스 내 일반 그리기(드래그) 시작 좌표
        private Point _startPoint;

        // 이미 캔버스에 배치된 노드를 마우스로 잡고 이동할 때 사용하는 변수들
        private bool _isNodeDragging = false;
        private Point _nodeClickOffset; // 마우스 커서와 노드 좌상단 간의 거리 보정값
        private FrameworkElement? _draggedNodeElement = null;
        private Rect _nodeDragStartRect;

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
        private ConnectionWorkspaceViewModel? _renamingWorkspace;

        // 탭 스크롤 좌우 이동 거리 상수
        private const double SCROLL_OFFSET = 400.0;

        // --- 2. 그룹핑(Grouping) 관련 변수 ---
        // 사용자가 직접 화면에 박스를 그려서 그룹/네트워크를 묶는 모드 활성화 플래그
        private bool _isGroupingMode = false;
        private bool _isNetworkDrawingMode = false;
        private DockerNetworkGroup? _pendingExistingNetwork;

        // 이미 만들어진 그룹 전체를 잡고 이동할 때 사용하는 상태 변수들
        private bool _isGroupMoving = false;
        private GroupViewModel? _movingGroup = null;
        private Point _groupClickOffset;
        private Rect _groupMoveStartRect;

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
        /// MainWindow를 초기화하고 ViewModel과 다이얼로그 서비스를 주입받습니다.
        /// 앱 전반의 백그라운드 타이머(도커 상태 감시, 자동 저장)를 세팅합니다.
        /// </summary>
        public MainWindow(MainViewModel viewModel, IDialogService dialogService)
        {
            InitializeComponent();

            // 1. 다이얼로그 서비스 할당 (Null 체크 유지)
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

            // 2. 데이터 컨텍스트 설정
            this.DataContext = viewModel;
            viewModel.SheetManager.PropertyChanged += SheetManager_PropertyChanged;
            this.Closed += Window_Closed;

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

            if (vm.SheetManager.HasActiveCreationTasks)
            {
                int count = vm.SheetManager.ActiveCreationTaskCount;
                bool closeAnyway = _dialogService.ShowConfirm(
                    $"현재 생성 작업이 진행 중입니다. ({count}개)\n앱을 종료하시겠습니까?\n\n종료하면 Docker 리소스가 일부 생성된 상태로 남을 수 있습니다.",
                    "작업 진행 중");

                if (!closeAnyway)
                {
                    e.Cancel = true;
                    return;
                }
            }

            if (!vm.IsModified) return;

            var result = _dialogService.ShowYesNoCancel("변경 사항이 저장되지 않았습니다.\n저장하고 종료하시겠습니까?", "종료 확인");

            if (result == DialogChoice.Cancel)
            {
                e.Cancel = true;
            }
            else if (result == DialogChoice.Yes)
            {
                if (!string.IsNullOrEmpty(vm.CurrentFilePath))
                {
                    DiagramSaveResult saveResult = FileService.QuickSaveWithResult(vm, vm.CurrentFilePath);
                    if (!saveResult.Success)
                    {
                        _dialogService.ShowError(saveResult.GetUserMessage(vm.CurrentFilePath), "저장 실패");
                        e.Cancel = true;
                    }
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

        private void Window_Closed(object? sender, EventArgs e)
        {
            _dockerMonitorTimer.Stop();
            _dockerMonitorTimer.Tick -= DockerMonitorTimer_Tick;
            _autoSaveTimer.Stop();
            _autoSaveTimer.Tick -= AutoSaveTimer_Tick;
            Closed -= Window_Closed;

            if (DataContext is MainViewModel viewModel)
            {
                viewModel.SheetManager.PropertyChanged -= SheetManager_PropertyChanged;
            }

            if (DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        private void SheetManager_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SheetManagerViewModel.ActiveSheet)) return;

            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(CenterActiveSheetIfNeeded));
        }

        private void CenterActiveSheetIfNeeded()
        {
            if (!IsLoaded || DataContext is not MainViewModel viewModel) return;

            SheetViewModel? sheet = viewModel.ActiveSheet;
            if (sheet == null || sheet.IsViewportInitialized) return;
            if (ViewportCanvas.ActualWidth <= 0 || ViewportCanvas.ActualHeight <= 0) return;

            bool restored = sheet.RestoreViewportOffset(
                ViewportCanvas.ActualWidth,
                ViewportCanvas.ActualHeight);

            if (!restored)
            {
                sheet.OffsetX = (ViewportCanvas.ActualWidth - (sheet.MapWidth * sheet.Scale)) / 2.0;
                sheet.OffsetY = (ViewportCanvas.ActualHeight - (sheet.MapHeight * sheet.Scale)) / 2.0;
            }

            sheet.IsViewportInitialized = true;
            sheet.CaptureViewportCenter(ViewportCanvas.ActualWidth, ViewportCanvas.ActualHeight);
        }

        private void CaptureActiveSheetViewportCenter(bool markModified)
        {
            if (DataContext is not MainViewModel viewModel ||
                viewModel.ActiveSheet is not SheetViewModel sheet)
            {
                return;
            }

            bool changed = sheet.CaptureViewportCenter(
                ViewportCanvas.ActualWidth,
                ViewportCanvas.ActualHeight);

            if (changed && markModified)
                viewModel.SheetManager.MarkAsModified();
        }

        private void ViewportCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (DataContext is not MainViewModel viewModel ||
                viewModel.ActiveSheet is not SheetViewModel sheet ||
                !sheet.IsViewportInitialized)
            {
                return;
            }

            sheet.RestoreViewportOffset(e.NewSize.Width, e.NewSize.Height);
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

            DiagramSaveResult result = FileService.QuickSaveWithResult(vm, vm.CurrentFilePath);
            if (result.Success)
            {
                Debug.WriteLine($"[DockerDiscovery] Saved to {vm.CurrentFilePath} at {DateTime.Now}");
                vm.IsModified = false;
            }
            else
            {
                Debug.WriteLine($"[DockerDiscovery] Auto-save failed for '{vm.CurrentFilePath}'. {result.ErrorMessage}");
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
            CenterActiveSheetIfNeeded();

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

        private Point GetViewportWorldCenter()
        {
            var transformParent = (UIElement)ZoomPanGrid.Parent;
            Point viewportCenter = ViewportCanvas.TranslatePoint(
                new Point(ViewportCanvas.ActualWidth / 2.0, ViewportCanvas.ActualHeight / 2.0),
                transformParent);
            return ZoomPanGrid.RenderTransform.Inverse?.Transform(viewportCenter)
                ?? viewportCenter;
        }

        private Point GetViewportCenteredPlacement(double itemWidth, double itemHeight)
        {
            Point worldCenter = GetViewportWorldCenter();

            double x = Math.Round((worldCenter.X - (itemWidth / 2.0)) / 10.0) * 10.0;
            double y = Math.Round((worldCenter.Y - (itemHeight / 2.0)) / 10.0) * 10.0;

            if (DataContext is MainViewModel viewModel && viewModel.ActiveSheet is SheetViewModel sheet)
            {
                double maximumX = Math.Max(0, sheet.MapWidth - itemWidth);
                double maximumY = Math.Max(0, sheet.MapHeight - itemHeight);
                x = Math.Clamp(x, 0, maximumX);
                y = Math.Clamp(y, 0, maximumY);
            }

            return new Point(x, y);
        }

        // --- 키보드 입력 ---

        /// <summary>
        /// 캔버스 위에서 사용자가 키보드의 'Delete' 키를 눌렀을 때 선택된 항목(노드, 선, 그룹 등)을 즉시 삭제하는 전역 단축키 처리기입니다.
        /// </summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && CancelAreaDrawingMode())
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Delete)
            {
                (DataContext as MainViewModel)?.Inspector.DeleteCommand.Execute(null);
                e.Handled = true;
            }
        }

        private bool CancelAreaDrawingMode()
        {
            if (!_isGroupingMode && !_isNetworkDrawingMode)
                return false;

            if (ViewportCanvas.IsMouseCaptured)
                ViewportCanvas.ReleaseMouseCapture();

            if (TempGroupRect != null)
            {
                TempGroupRect.Visibility = Visibility.Collapsed;
                TempGroupRect.Width = 0;
                TempGroupRect.Height = 0;
            }

            _isGroupingMode = false;
            _isNetworkDrawingMode = false;
            _pendingExistingNetwork = null;
            Mouse.OverrideCursor = null;
            return true;
        }

        // --- 헬퍼 메서드들 ---

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

    }
}
