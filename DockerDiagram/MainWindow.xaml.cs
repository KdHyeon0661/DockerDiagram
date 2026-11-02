using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Diagram;
using DockerDiagram.Infrastructure;
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
    public partial class MainWindow : Window, IDisposable
    {
        // --- 1. 기본 변수들 ---
        // 사이드바에서 아이템을 캔버스로 끌어올 때(Drag & Drop) 사용하는 시작점 및 드래그 상태
        private Point _toolStartPoint;
        private bool _isToolDragging;
        private bool _isStackTemplateDragging;
        private bool _isComposeProjectDragging;

        // 캔버스 내 일반 그리기(드래그) 시작 좌표
        private Point _startPoint;

        // 이미 캔버스에 배치된 노드를 마우스로 잡고 이동할 때 사용하는 변수들
        private bool _isNodeDragging;
        private Point _nodeClickOffset; // 마우스 커서와 노드 좌상단 간의 거리 보정값
        private FrameworkElement? _draggedNodeElement;
        private Rect _nodeDragStartRect;

        // 컨테이너 포트 간 선(Connector)을 직접 그을 때 사용하는 상태 변수들
        private bool _isConnecting;
        private IConnectableItem? _sourceItem;
        private Point _startPointCanvas;
        private Border? _lastHitPort;
        private PortDirection _sourceDir = PortDirection.None;

        // 노드나 그룹의 모서리를 잡아당겨 크기를 조절(Resizing)할 때 사용하는 변수들
        private bool _isResizing;
        private string _resizeDir = "";
        private NodeViewModel? _resizingNode;
        private Point _resizeStartWorldPos;
        private Rect _resizeStartNodeRect;

        // 마우스 우클릭으로 캔버스 전체 화면을 이동(Panning)시킬 때 사용하는 변수들
        private bool _isPanning;
        private Point _panStartClick;
        private Point _panStartTranslate;

        // 상단 멀티 탭(시트) 순서를 드래그해서 바꿀 때 사용하는 상태 변수들
        private Point _sheetDragStartPoint;
        private bool _isSheetDragging;
        private bool _isClickedOnTab;
        private SheetViewModel? _renamingSheet;
        private ConnectionWorkspaceViewModel? _renamingWorkspace;

        // 탭 스크롤 좌우 이동 거리 상수
        private const double SCROLL_OFFSET = 400.0;

        // --- 2. 그룹핑(Grouping) 관련 변수 ---
        // 사용자가 직접 화면에 박스를 그려서 그룹/네트워크를 묶는 모드 활성화 플래그
        private bool _isGroupingMode;
        private bool _isNetworkDrawingMode;
        private DockerNetworkGroup? _pendingExistingNetwork;
        private RuntimeResourceKind? _pendingSwarmDraftGroupKind;

        // 이미 만들어진 그룹 전체를 잡고 이동할 때 사용하는 상태 변수들
        private bool _isGroupMoving;
        private GroupViewModel? _movingGroup;
        private Point _groupClickOffset;
        private Rect _groupMoveStartRect;

        // 임시 다중 선택과 선택 집합 이동 상태
        private bool _isMarqueeSelecting;
        private Point _marqueeStartWorld;
        private bool _marqueePreserveSelection;
        private ConnectableItemViewModel? _marqueeClickFallbackItem;
        private bool _isSelectionMoving;
        private Point _selectionMoveLastWorld;
        private FrameworkElement? _selectionMoveCaptureElement;
        private Dictionary<NodeViewModel, Rect> _selectionMoveBeforeNodes = [];
        private Dictionary<GroupViewModel, Rect> _selectionMoveBeforeGroups = [];
        private HashSet<NodeViewModel> _selectionMoveNodes = [];
        private HashSet<NodeViewModel> _selectionMoveMembershipNodes = [];
        private List<GroupViewModel> _selectionMoveGroups = [];

        // --- 3. 재연결(Reconnection) 관련 변수 ---
        // 이미 그어진 선(Connector)의 끝점을 잡고 다른 곳으로 연결을 옮길 때 사용하는 상태 변수들
        private bool _isReconnecting;
        private ConnectorViewModel? _reconnectingConn;
        private string _reconnectType = "";

        // 그룹 리사이징 정보 보관용
        private GroupViewModel? _resizingGroup;
        private Rect _resizeStartGroupRect;

        // 백그라운드 상태 모니터링 타이머들
        private DispatcherTimer _dockerMonitorTimer;
        private DispatcherTimer _autoSaveTimer;
        private readonly ApplicationLifetimeCoordinator _lifetime = new();
        private CancellationTokenSource? _dockerStartupCts;
        private TaskCompletionSource<bool>? _dockerStartupPromptCompletion;
        private string? _recoveryFilePath;
        private long _recoveryEpoch;
        private int _autoSaveInProgress;
        private int _dockerHealthFailureCount;
        private bool _disposed;


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
            _dockerMonitorTimer.Interval = DockerConnectionTiming.HealthPollInterval;
            _dockerMonitorTimer.Tick += DockerMonitorTimer_Tick;

            _autoSaveTimer = new DispatcherTimer();
            _autoSaveTimer.Interval = RuntimeSyncTiming.AutoSaveInterval;
            _autoSaveTimer.Tick += AutoSaveTimer_Tick;
            _lifetime.Register(CleanupWindowResources);
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

            if (!vm.IsModified)
            {
                DeleteActiveRecovery();
                return;
            }

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
            else
            {
                // 사용자가 명시적으로 변경 사항을 버렸으므로 복구본도 제거합니다.
                vm.IsModified = false;
                DeleteActiveRecovery();
            }
        }

        private void Window_Closed(object? sender, EventArgs e)
            => Dispose();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _lifetime.Dispose();
            GC.SuppressFinalize(this);
        }

        private void CleanupWindowResources()
        {
            _dockerStartupPromptCompletion?.TrySetResult(false);
            _dockerStartupCts?.Cancel();
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
            if (e.PropertyName == nameof(SheetManagerViewModel.IsModified) &&
                sender is SheetManagerViewModel { IsModified: false })
            {
                DeleteActiveRecovery();
                return;
            }

            if (e.PropertyName == nameof(SheetManagerViewModel.CurrentFilePath))
            {
                UpdateTitle();
                return;
            }

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
        /// 설정된 주기(기본 30초)마다 실제 프로젝트 파일과 분리된 복구 스냅샷을 저장합니다.
        /// </summary>
        private async void AutoSaveTimer_Tick(object? sender, EventArgs e)
        {
            var vm = this.DataContext as MainViewModel;
            if (vm == null) return;

            if (!vm.IsModified) return;
            if (Interlocked.Exchange(ref _autoSaveInProgress, 1) == 1) return;

            long recoveryEpoch = Interlocked.Read(ref _recoveryEpoch);
            try
            {
                DiagramRecoverySaveResult recoveryResult = await FileService.SaveRecoverySnapshotAsync(
                    vm,
                    _recoveryFilePath,
                    _lifetime.Token);
                if (recoveryResult.SaveResult.Success)
                {
                    if (!vm.IsModified ||
                        recoveryEpoch != Interlocked.Read(ref _recoveryEpoch))
                    {
                        // 복구 저장 도중 사용자가 실제 저장·폐기·프로젝트 초기화를 완료한 경우
                        // 이전 상태의 저장 작업이 뒤늦게 복구본을 되살리지 않도록 결과 파일만 제거합니다.
                        FileService.DeleteRecoveryFile(recoveryResult.RecoveryPath);
                    }
                    else
                    {
                        _recoveryFilePath = recoveryResult.RecoveryPath;
                        AppLog.Info($"프로젝트 복구 스냅샷 저장 완료: {_recoveryFilePath}");
                    }
                }
                else if (!_lifetime.Token.IsCancellationRequested)
                {
                    AppLog.Warning($"프로젝트 복구 스냅샷 저장 실패: {recoveryResult.SaveResult.ErrorMessage}");
                }
            }
            finally
            {
                Interlocked.Exchange(ref _autoSaveInProgress, 0);
            }
        }

        /// <summary>
        /// 윈도우 UI가 메모리에 성공적으로 로드된 직후 실행됩니다.
        /// 상태 모니터링 타이머들을 시작하고, 도커 프로세스 생존 여부를 최초로 검사합니다.
        /// </summary>
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            InitializeSwarmStackController();
            UpdateScrollButtonsState();

            if (ViewModel == null) return;

            IsEnabled = false;
            try
            {
                await ViewModel.InitializeAsync();
                await TryRestoreRecoveryAsync();
                UpdateTitle();
                CenterActiveSheetIfNeeded();

                // Docker 시작 상태 오버레이의 취소 버튼을 사용할 수 있도록
                // 초기 파일 복원이 끝난 뒤에는 메인 창을 다시 활성화합니다.
                IsEnabled = true;
                bool dockerReady = await CheckDockerStateAsync();
                await ViewModel.SheetManager.RestoreLiveStateAsync(
                    dockerReady ? null : ViewModel.ActiveSheet?.DockerService);

                if (dockerReady && !_dockerMonitorTimer.IsEnabled) _dockerMonitorTimer.Start();
                if (!_autoSaveTimer.IsEnabled) _autoSaveTimer.Start();
            }
            catch (Exception ex)
            {
                AppLog.Error("메인 화면 초기화 실패", ex);
                _dialogService.ShowError(
                    $"프로그램을 초기화하지 못했습니다.\n\n{ex.GetBaseException().Message}\n\n" +
                    $"진단 로그: {AppLog.LogFilePath}",
                    "초기화 실패");
            }
            finally
            {
                IsEnabled = true;
            }
        }

        private async Task TryRestoreRecoveryAsync()
        {
            foreach (DiagramRecoveryCandidate candidate in FileService.GetRecoveryCandidates())
            {
                string sourceText = string.IsNullOrWhiteSpace(candidate.OriginalFilePath)
                    ? "저장되지 않은 새 프로젝트"
                    : candidate.OriginalFilePath;
                bool recover = _dialogService.ShowConfirm(
                    $"비정상 종료 후 남은 프로젝트 복구본이 있습니다.\n\n" +
                    $"대상: {sourceText}\n" +
                    $"복구 시각: {candidate.CapturedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n\n" +
                    "복구하시겠습니까? 아니오를 선택하면 이 복구본은 삭제됩니다.",
                    "프로젝트 복구");

                if (!recover)
                {
                    FileService.DeleteRecoveryFile(candidate.RecoveryPath);
                    continue;
                }

                IDockerService activeService = ViewModel.ActiveSheet?.DockerService ??
                                                 ViewModel.SheetManager.DefaultDockerService;
                bool loaded = await FileService.LoadDiagramFromPathAsync(
                    ViewModel,
                    candidate.RecoveryPath,
                    (IContainerService)activeService,
                    (IVolumeService)activeService,
                    (INetworkService)activeService,
                    _dialogService);
                if (!loaded)
                    continue;

                _recoveryFilePath = candidate.RecoveryPath;
                ViewModel.CurrentFilePath = !string.IsNullOrWhiteSpace(candidate.OriginalFilePath) &&
                                            File.Exists(candidate.OriginalFilePath)
                    ? candidate.OriginalFilePath
                    : null;
                ViewModel.IsModified = true;
                return;
            }
        }

        private void DeleteActiveRecovery()
        {
            Interlocked.Increment(ref _recoveryEpoch);
            FileService.DeleteRecoveryFile(_recoveryFilePath);
            _recoveryFilePath = null;
        }

        /// <summary>
        /// 백그라운드에서 주기적으로 도커(Docker Desktop) 엔진이 꺼져있는지 감시하는 타이머 핸들러입니다.
        /// </summary>
        private async void DockerMonitorTimer_Tick(object? sender, EventArgs e)
        {
            SheetViewModel? activeSheet = ViewModel.ActiveSheet;
            if (activeSheet?.Profile.Type != EndpointType.Local)
            {
                _dockerHealthFailureCount = 0;
                return;
            }

            _dockerMonitorTimer.Stop();
            bool shouldContinue = true;
            try
            {
                if (await activeSheet.DockerService.PingAsync())
                {
                    _dockerHealthFailureCount = 0;
                    return;
                }

                _dockerHealthFailureCount++;
                if (_dockerHealthFailureCount < DockerConnectionTiming.HealthFailureThreshold) return;

                _dockerHealthFailureCount = 0;
                shouldContinue = await CheckDockerStateAsync();
            }
            catch (Exception ex)
            {
                shouldContinue = false;
                AppLog.Warning("주기적 Docker 상태 확인 실패", ex);
                if (!_lifetime.Token.IsCancellationRequested)
                {
                    _dialogService.ShowError(
                        $"Docker 상태를 확인하지 못했습니다.\n{ex.GetBaseException().Message}\n\nDocker 연결 없이 프로그램을 계속 사용합니다.",
                        "Docker 상태 확인 실패");
                }
            }
            finally
            {
                if (shouldContinue && !_lifetime.Token.IsCancellationRequested && IsLoaded)
                    _dockerMonitorTimer.Start();
            }
        }

        /// <summary>
        /// 도커 엔진이 꺼져있음을 감지했을 때 사용자에게 알림을 띄우고, 승인 시 도커를 다시 실행시켜주는 자동 복구(Failover) 로직입니다.
        /// </summary>
        private async Task<bool> CheckDockerStateAsync()
        {
            SheetViewModel? activeSheet = ViewModel.ActiveSheet;
            if (activeSheet?.Profile.Type != EndpointType.Local) return true;
            if (await activeSheet.DockerService.PingAsync()) return true;

            bool dockerProcessRunning = DockerServiceHelper.IsDockerRunning();
            string unavailableReason = dockerProcessRunning
                ? "Docker Desktop 프로세스는 실행 중이지만 Docker 엔진이 응답하지 않습니다."
                : "내 PC의 Docker Desktop이 종료되었습니다.";

            bool result = await ShowDockerStartupPromptAsync(
                dockerProcessRunning,
                unavailableReason);

            if (!result)
            {
                HideDockerStartupOverlay();
                return false;
            }

            using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime.Token);
            _dockerStartupCts = startupCts;
            ShowDockerStartupOverlay();
            var progress = new Progress<DockerStartupProgress>(UpdateDockerStartupProgress);

            try
            {
                await DockerServiceHelper.StartDockerAsync(
                    activeSheet.DockerService,
                    progress,
                    startupCts.Token);

                if (DataContext is MainViewModel vm)
                {
                    DockerStartupPhaseText.Text = "Docker 연결 완료";
                    DockerStartupDetailText.Text = "Docker 리소스를 최신 상태로 동기화하는 중입니다.";
                    try
                    {
                        await vm.OnDockerStartedAsync();
                    }
                    catch (Exception syncException)
                    {
                        AppLog.Warning("Docker 시작 후 리소스 동기화 실패", syncException);
                        HideDockerStartupOverlay();
                        _dialogService.ShowError(
                            $"Docker 엔진 연결은 완료되었지만 리소스 동기화에 실패했습니다.\n" +
                            $"{syncException.GetBaseException().Message}",
                            "Docker 동기화 실패");
                    }
                }

                return true;
            }
            catch (OperationCanceledException) when (startupCts.IsCancellationRequested)
            {
                if (!_lifetime.Token.IsCancellationRequested)
                {
                    AppLog.Info("사용자가 Docker Desktop 시작 대기를 중지했습니다.");
                    HideDockerStartupOverlay();
                    _dialogService.ShowInfo(
                        "Docker Desktop 시작 대기를 중지했습니다.\n프로그램은 Docker 연결 없이 계속 사용할 수 있습니다.",
                        "Docker 대기 중지");
                }
                return false;
            }
            catch (Exception ex)
            {
                AppLog.Error("Docker Desktop 복구 실패", ex);
                HideDockerStartupOverlay();
                _dialogService.ShowError(
                    $"Docker Desktop을 시작하지 못했습니다.\n{ex.GetBaseException().Message}\n\nDocker 연결 없이 프로그램을 계속 사용합니다.",
                    "Docker 실행 실패");
                return false;
            }
            finally
            {
                if (ReferenceEquals(_dockerStartupCts, startupCts))
                    _dockerStartupCts = null;
                HideDockerStartupOverlay();
            }
        }

        private async Task<bool> ShowDockerStartupPromptAsync(
            bool dockerProcessRunning,
            string unavailableReason)
        {
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _dockerStartupPromptCompletion = completion;

            DockerStartupTitleText.Text = dockerProcessRunning
                ? "Docker Engine 응답 없음"
                : "Docker Desktop이 꺼져 있습니다";
            DockerStartupPhaseText.Text = unavailableReason;
            DockerStartupDetailText.Text = dockerProcessRunning
                ? "Docker Desktop은 실행 중이지만 엔진 연결이 아직 준비되지 않았습니다. 준비 상태를 계속 확인할 수 있습니다."
                : "Docker Desktop을 실행하고 엔진 연결이 준비될 때까지 이 화면에서 상태를 확인할 수 있습니다.";
            DockerStartupStartButton.Content = dockerProcessRunning
                ? "준비 상태 확인"
                : "Docker Desktop 실행";
            SetDockerStartupStatus("연결 필요", isReady: false);
            DockerStartupPromptPanel.Visibility = Visibility.Visible;
            DockerStartupProgressPanel.Visibility = Visibility.Collapsed;
            DockerStartupPromptActions.Visibility = Visibility.Visible;
            DockerStartupProgressActions.Visibility = Visibility.Collapsed;
            DockerStartupCancelButton.IsEnabled = true;
            DockerStartupOverlay.Visibility = Visibility.Visible;

            try
            {
                return await completion.Task.WaitAsync(_lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.Token.IsCancellationRequested)
            {
                return false;
            }
            finally
            {
                if (ReferenceEquals(_dockerStartupPromptCompletion, completion))
                    _dockerStartupPromptCompletion = null;
            }
        }

        private void ShowDockerStartupOverlay()
        {
            DockerStartupTitleText.Text = "Docker Desktop 시작 중";
            DockerStartupPhaseText.Text = "Docker 상태 확인 중";
            DockerStartupDetailText.Text = "Docker 엔진 응답을 확인하고 있습니다.";
            DockerStartupElapsedText.Text = "경과 시간 0:00";
            SetDockerStartupStatus("시작 중", isReady: false);
            DockerStartupPromptPanel.Visibility = Visibility.Collapsed;
            DockerStartupProgressPanel.Visibility = Visibility.Visible;
            DockerStartupPromptActions.Visibility = Visibility.Collapsed;
            DockerStartupProgressActions.Visibility = Visibility.Visible;
            DockerStartupCancelButton.IsEnabled = true;
            DockerStartupOverlay.Visibility = Visibility.Visible;
        }

        private void HideDockerStartupOverlay()
        {
            DockerStartupOverlay.Visibility = Visibility.Collapsed;
            DockerStartupCancelButton.IsEnabled = true;
        }

        private void UpdateDockerStartupProgress(DockerStartupProgress progress)
        {
            DockerStartupPhaseText.Text = progress.Phase switch
            {
                DockerStartupPhase.Checking => "Docker 상태 확인 중",
                DockerStartupPhase.Launching => "Docker Desktop 실행 중",
                DockerStartupPhase.ProcessRunning => "Docker Desktop 프로세스 실행 중",
                DockerStartupPhase.WaitingForEngine => "Docker 엔진 준비 중",
                DockerStartupPhase.Ready => "Docker 연결 완료",
                _ => "Docker 상태 확인 중"
            };
            DockerStartupTitleText.Text = progress.Phase == DockerStartupPhase.Ready
                ? "Docker 연결 완료"
                : "Docker Desktop 시작 중";
            DockerStartupDetailText.Text = progress.Message;
            DockerStartupElapsedText.Text = $"경과 시간 {FormatElapsed(progress.Elapsed)}";
            SetDockerStartupStatus(
                progress.Phase == DockerStartupPhase.Ready ? "연결됨" : "시작 중",
                progress.Phase == DockerStartupPhase.Ready);
        }

        private void SetDockerStartupStatus(string text, bool isReady)
        {
            DockerStartupStatusText.Text = text;
            DockerStartupStatusBadge.Background = (Brush)FindResource(
                isReady ? "DialogSuccessSurfaceBrush" : "DialogWarningSurfaceBrush");
            DockerStartupStatusBadge.BorderBrush = (Brush)FindResource(
                isReady ? "DialogSuccessBorderBrush" : "DialogWarningBorderBrush");
            DockerStartupStatusText.Foreground = (Brush)FindResource(
                isReady ? "DialogSuccessTextBrush" : "DialogWarningTextBrush");
        }

        private static string FormatElapsed(TimeSpan elapsed) =>
            elapsed.TotalHours >= 1
                ? elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : elapsed.ToString(@"m\:ss", CultureInfo.InvariantCulture);

        private void DockerStartupCancel_Click(object sender, RoutedEventArgs e)
        {
            DockerStartupCancelButton.IsEnabled = false;
            DockerStartupPhaseText.Text = "대기 중지 중";
            DockerStartupDetailText.Text = "진행 중인 Docker 상태 확인을 중지하고 있습니다.";
            _dockerStartupCts?.Cancel();
        }

        private void DockerStartupStart_Click(object sender, RoutedEventArgs e)
        {
            _dockerStartupPromptCompletion?.TrySetResult(true);
        }

        private void DockerStartupOffline_Click(object sender, RoutedEventArgs e)
        {
            _dockerStartupPromptCompletion?.TrySetResult(false);
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

            double x = DiagramMetrics.SnapToGrid(worldCenter.X - (itemWidth / 2.0));
            double y = DiagramMetrics.SnapToGrid(worldCenter.Y - (itemHeight / 2.0));

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
        private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && CancelAreaDrawingMode())
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Delete && !IsTextEditingControl(Keyboard.FocusedElement))
            {
                if (DataContext is MainViewModel viewModel)
                    await viewModel.Inspector.DeleteSelectedAsync();
                e.Handled = true;
            }
        }

        private static bool IsTextEditingControl(IInputElement? focusedElement)
        {
            if (focusedElement is TextBoxBase or PasswordBox)
                return true;

            DependencyObject? current = focusedElement as DependencyObject;
            while (current != null)
            {
                if (current is TextBoxBase or PasswordBox)
                    return true;
                if (current is ComboBox { IsEditable: true })
                    return true;
                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }

        private bool CancelAreaDrawingMode()
        {
            if (_isSelectionMoving)
            {
                foreach (var pair in _selectionMoveBeforeNodes)
                {
                    pair.Key.X = pair.Value.X;
                    pair.Key.Y = pair.Value.Y;
                    pair.Key.Width = pair.Value.Width;
                    pair.Key.Height = pair.Value.Height;
                }

                foreach (var pair in _selectionMoveBeforeGroups)
                {
                    pair.Key.X = pair.Value.X;
                    pair.Key.Y = pair.Value.Y;
                    pair.Key.Width = pair.Value.Width;
                    pair.Key.Height = pair.Value.Height;
                }

                _selectionMoveCaptureElement?.ReleaseMouseCapture();
                ResetSelectionMoveState();
                return true;
            }

            if (_isMarqueeSelecting)
            {
                if (ViewportCanvas.IsMouseCaptured)
                    ViewportCanvas.ReleaseMouseCapture();
                MultiSelectionRect.Visibility = Visibility.Collapsed;
                _isMarqueeSelecting = false;
                _marqueeClickFallbackItem = null;
                return true;
            }

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
            _pendingSwarmDraftGroupKind = null;
            Mouse.OverrideCursor = null;
            return true;
        }

        // --- 헬퍼 메서드들 ---

        /// <summary>
        /// WPF 시각적 트리(Visual Tree)를 거슬러 올라가며 조건(Predicate)을 만족하는 특정 타입의 부모 요소를 찾습니다.
        /// 마우스 클릭 이벤트에서 클릭된 UI 조각(HitTest 결과)이 어떤 뷰모델 객체에 속하는지 역추적할 때 사용됩니다.
        /// </summary>
        private static T? FindParent<T>(DependencyObject? child, Func<T, bool> predicate) where T : DependencyObject
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
        private static PortDirection GetClosestDirection(Point mouse, Rect rect)
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
        private static Rect GetGroupContentBounds(GroupViewModel group)
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
