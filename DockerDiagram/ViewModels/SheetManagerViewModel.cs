using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    /// <summary>
    /// 접속 대상(Local/SSH)별 상위 탭과, 각 접속 대상 안의 맵/시트 탭을 함께 관리합니다.
    /// 기존 화면의 ActiveSheet 중심 바인딩은 유지해 캔버스 로직과의 결합을 최소화합니다.
    /// </summary>
    public class SheetManagerViewModel : ViewModelBase, IDisposable
    {
        private readonly MainViewModel _mainVm;
        private readonly IDockerService _defaultDockerService;
        private readonly IDialogService _dialogService;
        private readonly ObservableCollection<SheetViewModel> _emptySheets = [];

        // --- 1. 상태 및 데이터 ---
        public ObservableCollection<ConnectionWorkspaceViewModel> Workspaces { get; } = [];
        internal IDockerService DefaultDockerService => _defaultDockerService;

        private ConnectionWorkspaceViewModel? _activeWorkspace;
        private bool _isWorkspaceLayer = true;
        private bool _suppressCreationSwitchPrompt;
        private bool _disposed;

        public bool IsWorkspaceLayer
        {
            get => _isWorkspaceLayer;
            set
            {
                if (SetProperty(ref _isWorkspaceLayer, value))
                {
                    OnPropertyChanged(nameof(IsSheetLayer));
                }
            }
        }

        public bool IsSheetLayer => !IsWorkspaceLayer;

        public ConnectionWorkspaceViewModel? ActiveWorkspace
        {
            get => _activeWorkspace;
            set
            {
                if (!_suppressCreationSwitchPrompt && value != _activeWorkspace && HasActiveCreationTasks && !ConfirmLeaveActiveCreationTasks("작업 중인 시트를 벗어나시겠습니까?"))
                {
                    OnPropertyChanged();
                    return;
                }

                if (SetProperty(ref _activeWorkspace, value))
                {
                    OnPropertyChanged(nameof(Sheets));
                    _suppressCreationSwitchPrompt = true;
                    try
                    {
                        ActiveSheet = _activeWorkspace?.ActiveSheet ?? _activeWorkspace?.Sheets.FirstOrDefault();
                    }
                    finally
                    {
                        _suppressCreationSwitchPrompt = false;
                    }
                    _mainVm.Explorer?.UpdateAvailableItems();
                }
            }
        }

        public void EnterWorkspace(ConnectionWorkspaceViewModel workspace)
        {
            ActiveWorkspace = workspace;
            IsWorkspaceLayer = false;
        }

        public void ShowWorkspaceLayer()
        {
            IsWorkspaceLayer = true;
        }

        public ObservableCollection<SheetViewModel> Sheets => ActiveWorkspace?.Sheets ?? _emptySheets;
        public IEnumerable<SheetViewModel> AllSheets => Workspaces.SelectMany(w => w.Sheets);
        public bool HasActiveCreationTasks => AllSheets.Any(sheet => sheet.Nodes.Any(node => node.IsBusyCreating));
        public int ActiveCreationTaskCount => AllSheets.Sum(sheet => sheet.Nodes.Count(node => node.IsBusyCreating));

        private SheetViewModel? _activeSheet;
        public SheetViewModel? ActiveSheet
        {
            get => _activeSheet;
            set
            {
                if (!_suppressCreationSwitchPrompt && value != _activeSheet && HasActiveCreationTasks && !ConfirmLeaveActiveCreationTasks("다른 시트로 이동하시겠습니까?"))
                {
                    OnPropertyChanged();
                    return;
                }

                if (value != null)
                {
                    var owner = FindWorkspaceContaining(value);
                    if (owner != null && owner != ActiveWorkspace)
                    {
                        ActiveWorkspace = owner;
                    }
                }

                if (_activeSheet != null) UnsubscribeSheetEvents(_activeSheet);

                _activeSheet = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MapWidth));
                OnPropertyChanged(nameof(MapHeight));
                _mainVm.RefreshRuntimeUiProfile();
                _mainVm.History?.Clear();

                if (_activeSheet != null && ActiveWorkspace != null && ActiveWorkspace.Sheets.Contains(_activeSheet))
                {
                    ActiveWorkspace.ActiveSheet = _activeSheet;
                }

                if (_mainVm.Inspector != null) _mainVm.Inspector.ClearSelection();

                if (_activeSheet != null)
                {
                    AttachSheetEvents();
                    _mainVm.Explorer?.UpdateAvailableItems();
                    _activeSheet.UpdateGroupLayering();
                }
            }
        }

        private bool _isModified;
        private long _modificationVersion;
        public bool IsModified
        {
            get => _isModified;
            set
            {
                if (value)
                    Interlocked.Increment(ref _modificationVersion);
                SetProperty(ref _isModified, value);
            }
        }

        internal long ModificationVersion => Interlocked.Read(ref _modificationVersion);

        internal void MarkSavedIfCurrent(long savedVersion)
        {
            if (Interlocked.Read(ref _modificationVersion) == savedVersion)
                IsModified = false;
        }

        private string? _currentFilePath;
        public string? CurrentFilePath { get => _currentFilePath; set => SetProperty(ref _currentFilePath, value); }

        public double MapWidth
        {
            get => ActiveSheet?.MapWidth ?? 2000;
            set { if (ActiveSheet != null) { ActiveSheet.MapWidth = value; OnPropertyChanged(); } }
        }

        public double MapHeight
        {
            get => ActiveSheet?.MapHeight ?? 2000;
            set { if (ActiveSheet != null) { ActiveSheet.MapHeight = value; OnPropertyChanged(); } }
        }

        // --- 2. 커맨드 (Commands) ---
        public ICommand AddSheetCommand { get; }
        public ICommand PrevSheetCommand { get; }
        public ICommand NextSheetCommand { get; }
        public ICommand ResetProjectCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand SaveAsCommand { get; }
        public ICommand LoadCommand { get; }

        public SheetManagerViewModel(MainViewModel mainVm, IDockerService defaultDockerService, IDialogService dialogService)
        {
            _mainVm = mainVm;
            _defaultDockerService = defaultDockerService;
            _dialogService = dialogService;

            AddSheetCommand = new RelayCommand(_ => AddSheet());
            PrevSheetCommand = new RelayCommand(_ => NavigateSheet(-1));
            NextSheetCommand = new RelayCommand(_ => NavigateSheet(1));
            ResetProjectCommand = new RelayCommand(ExecuteResetProject);

            SaveCommand = new RelayCommand(SaveAction);
            SaveAsCommand = new RelayCommand(SaveAsAction);
            LoadCommand = new AsyncRelayCommand(LoadActionAsync);
        }

        // --- 3. 파일 입출력 로직 ---
        private void SaveAction(object? obj)
        {
            if (!string.IsNullOrEmpty(CurrentFilePath))
            {
                DiagramSaveResult result = FileService.QuickSaveWithResult(_mainVm, CurrentFilePath);
                if (result.Success)
                {
                    _dialogService.ShowMessage("저장되었습니다.");
                    IsModified = false;
                }
                else
                {
                    _dialogService.ShowError(result.GetUserMessage(CurrentFilePath), "저장 실패");
                }
            }
            else
            {
                SaveAsAction(obj);
            }
        }

        private void SaveAsAction(object? obj)
        {
            string? savedPath = FileService.SaveDiagramAs(_mainVm, _dialogService);
            if (!string.IsNullOrEmpty(savedPath))
            {
                CurrentFilePath = savedPath;
                IsModified = false;
            }
        }

        private async Task LoadActionAsync(object? obj)
        {
            if (IsModified && !_dialogService.ShowConfirm("변경 사항이 저장되지 않았습니다. 계속하시겠습니까?", "확인"))
                return;

            var activeService = ActiveSheet?.DockerService ?? _defaultDockerService;
            string? loadedPath = await FileService.LoadDiagramWithDialogAsync(
                _mainVm, (IContainerService)activeService, (IVolumeService)activeService, (INetworkService)activeService, _dialogService);

            if (!string.IsNullOrEmpty(loadedPath))
            {
                CurrentFilePath = loadedPath;
                IsModified = false;
                await RestoreLiveStateAsync();
            }
        }

        public async Task LoadLastFileIfExistsAsync()
        {
            try
            {
                string lastPath = Properties.Settings.Default.LastFilePath;
                if (!string.IsNullOrEmpty(lastPath) && System.IO.File.Exists(lastPath))
                {
                    var activeService = ActiveSheet?.DockerService ?? _defaultDockerService;
                    bool success = await FileService.LoadDiagramFromPathAsync(
                        _mainVm, lastPath, (IContainerService)activeService, (IVolumeService)activeService, (INetworkService)activeService, _dialogService);

                    if (success)
                    {
                        CurrentFilePath = lastPath;
                        IsModified = false;
                    }
                }
            }
            catch (Exception ex) { AppLog.Warning("최근 다이어그램 자동 복원 실패", ex); }
        }

        public async Task RestoreLiveStateAsync(IDockerService? knownUnavailableService = null)
        {
            var serviceAvailability = new Dictionary<IDockerService, bool>(
                ReferenceEqualityComparer.Instance);
            if (knownUnavailableService != null)
                serviceAvailability[knownUnavailableService] = false;

            foreach (var sheet in AllSheets)
            {
                foreach (var group in sheet.Groups)
                    group.ParentSheet = sheet;

                foreach (var node in sheet.Nodes)
                    node.ParentSheet = sheet;

                // Kubernetes 저장 노드는 런타임 조회 없이 저장된 스냅샷 상태를 복원합니다.
                // Docker/Swarm 워크스페이스는 노드마다 실패를 반복하지 않도록 연결당 한 번만 확인합니다.
                if (sheet.RuntimeKind != RuntimeKind.Kubernetes)
                {
                    if (!serviceAvailability.TryGetValue(sheet.DockerService, out bool available))
                    {
                        try
                        {
                            available = await sheet.DockerService.PingAsync();
                        }
                        catch (Exception ex)
                        {
                            available = false;
                            AppLog.WarningThrottled(
                                $"restore-runtime-{sheet.Profile.ProfileId}",
                                $"'{sheet.Title}' 런타임 연결 확인 실패",
                                ex);
                        }

                        serviceAvailability[sheet.DockerService] = available;
                    }

                    if (!available)
                    {
                        MarkSheetRuntimeUnavailable(sheet);
                        continue;
                    }
                }

                sheet.IsRuntimeUnavailable = false;
                sheet.RuntimeStatusMessage = string.Empty;
                foreach (var node in sheet.Nodes)
                    await node.RefreshDetailsAsync();
            }
        }

        private static void MarkSheetRuntimeUnavailable(SheetViewModel sheet)
        {
            RuntimeAvailabilityMarker.MarkUnavailable(
                sheet,
                "저장된 다이어그램을 오프라인 스냅샷으로 표시합니다. 런타임 연결 후 다시 동기화하세요.");

            foreach (NodeViewModel node in sheet.Nodes.Where(node =>
                         !node.IsDraft && node.Type is NodeType.Container or NodeType.Volume))
            {
                node.SetDockerConnection(false);
            }

            foreach (NodeViewModel localReference in sheet.Nodes.Where(node =>
                         node.Type is NodeType.Internet or NodeType.HostPath))
            {
                localReference.SetDockerConnection(true);
            }

            foreach (GroupViewModel group in sheet.Groups.Where(group =>
                         !group.IsDraft && group.Type == GroupType.Network))
            {
                group.IsDockerConnected = false;
            }
        }

        // --- 4. 탭(Sheet) 관리 로직 ---
        public void AddSheet(bool markModified = true)
        {
            var workspace = ActiveWorkspace ?? EnsureLocalWorkspace();
            if (workspace.RuntimeKind == RuntimeKind.DockerSwarm)
            {
                AddSwarmStackSheet(markModified);
                return;
            }

            AddSheetToWorkspace(workspace, $"Sheet {workspace.Sheets.Count + 1}", activate: true);
            if (markModified)
                MarkAsModified();
        }

        public void AddSwarmStackSheet(bool markModified = true)
        {
            var workspace = ActiveWorkspace;
            if (workspace?.RuntimeKind != RuntimeKind.DockerSwarm) return;

            int ordinal = workspace.Sheets.Count(sheet => sheet.RuntimeKind == RuntimeKind.DockerSwarm) + 1;
            AddSheetToWorkspace(workspace, $"Stack {ordinal}", activate: true);
            if (markModified)
                MarkAsModified();
        }

        public ConnectionWorkspaceViewModel AddWorkspace(
            ConnectionProfile profile,
            IDockerService dockerService,
            bool activate = true,
            bool createInitialSheet = true,
            bool markModified = true)
        {
            var existing = FindWorkspaceByProfile(profile);
            if (existing != null)
            {
                if (dockerService != existing.DockerService &&
                    dockerService != _defaultDockerService &&
                    !IsServiceUsedByAnyWorkspace(dockerService))
                {
                    DisposeUnownedConnection(profile, dockerService);
                }

                if (activate) ActiveWorkspace = existing;
                return existing;
            }

            var workspace = new ConnectionWorkspaceViewModel(profile, dockerService);
            Workspaces.Add(workspace);
            _mainVm.DockerServiceFactory.Register(dockerService);

            if (createInitialSheet)
            {
                AddSheetToWorkspace(workspace, "Sheet 1", activate: false);
            }

            if (activate)
            {
                ActiveWorkspace = workspace;
            }

            if (markModified)
                MarkAsModified();

            return workspace;
        }

        public ConnectionWorkspaceViewModel CreateRuntimeWorkspace(
            ConnectionWorkspaceViewModel sourceWorkspace,
            RuntimeKind runtimeKind,
            bool activate = true)
        {
            if (sourceWorkspace.RuntimeKind == runtimeKind)
            {
                if (activate)
                {
                    ActiveWorkspace = sourceWorkspace;
                    ActiveSheet = sourceWorkspace.ActiveSheet ?? sourceWorkspace.Sheets.FirstOrDefault();
                }
                return sourceWorkspace;
            }

            var profile = CloneProfile(sourceWorkspace.Profile);
            profile.RuntimeKind = runtimeKind;

            var workspace = AddWorkspace(
                profile,
                sourceWorkspace.DockerService,
                activate: false,
                createInitialSheet: false,
                markModified: false);

            if (workspace.Sheets.Count == 0)
            {
                string initialTitle = runtimeKind == RuntimeKind.DockerSwarm
                    ? "Stack 1"
                    : $"{GetRuntimeLabel(runtimeKind)} Sheet 1";
                AddSheetToWorkspace(workspace, initialTitle, activate: false);
            }

            if (activate)
            {
                ActiveWorkspace = workspace;
                ActiveSheet = workspace.ActiveSheet ?? workspace.Sheets.FirstOrDefault();
                IsWorkspaceLayer = false;
            }

            MarkAsModified();
            return workspace;
        }

        public void AddExistingSheet(SheetViewModel sheet, bool activate = true)
        {
            ConfigureRuntimeRefresh(sheet);
            sheet.Profile.RuntimeKind = sheet.RuntimeKind;
            var workspace = FindWorkspaceByProfile(sheet.Profile);
            if (workspace == null)
            {
                workspace = new ConnectionWorkspaceViewModel(sheet.Profile, sheet.DockerService);
                Workspaces.Add(workspace);
                _mainVm.DockerServiceFactory.Register(sheet.DockerService);
            }
            else if (!ReferenceEquals(workspace.DockerService, sheet.DockerService))
            {
                throw new InvalidOperationException("같은 워크스페이스의 시트는 하나의 Docker 연결 서비스를 공유해야 합니다.");
            }

            workspace.Sheets.Add(sheet);
            workspace.ActiveSheet ??= sheet;

            if (activate)
            {
                ActiveWorkspace = workspace;
                ActiveSheet = sheet;
            }

            OnPropertyChanged(nameof(Sheets));
        }

        private SheetViewModel AddSheetToWorkspace(ConnectionWorkspaceViewModel workspace, string title, bool activate)
        {
            var newSheet = new SheetViewModel(title, workspace.Profile, workspace.DockerService, _dialogService, workspace.RuntimeKind);
            ConfigureRuntimeRefresh(newSheet);
            workspace.Sheets.Add(newSheet);
            workspace.ActiveSheet = newSheet;

            if (activate)
            {
                ActiveWorkspace = workspace;
                ActiveSheet = newSheet;
            }

            OnPropertyChanged(nameof(Sheets));
            return newSheet;
        }

        private void ConfigureRuntimeRefresh(SheetViewModel sheet)
        {
            sheet.RuntimeResourcesChangedAsync = async () =>
            {
                if (ReferenceEquals(ActiveSheet, sheet) && _mainVm.Explorer != null)
                    await _mainVm.RefreshRuntimeResourcesAsync();
            };
        }

        public void DeleteSheet(SheetViewModel sheet)
        {
            var workspace = FindWorkspaceContaining(sheet);
            if (workspace == null || workspace.Sheets.Count <= 1) return;

            if (ActiveSheet == sheet)
            {
                int index = workspace.Sheets.IndexOf(sheet);
                int nextIndex = index > 0 ? index - 1 : index + 1;
                ActiveSheet = workspace.Sheets[nextIndex];
            }

            DetachSheet(sheet);
            workspace.Sheets.Remove(sheet);
            OnPropertyChanged(nameof(Sheets));
            MarkAsModified();
        }

        private void NavigateSheet(int direction)
        {
            if (ActiveSheet == null || Sheets.Count <= 1) return;
            int currentIndex = Sheets.IndexOf(ActiveSheet);
            int newIndex = currentIndex + direction;
            if (newIndex >= 0 && newIndex < Sheets.Count) ActiveSheet = Sheets[newIndex];
        }

        private void ExecuteResetProject(object? obj)
        {
            if (HasActiveCreationTasks)
            {
                int count = ActiveCreationTaskCount;
                bool continueReset = _dialogService.ShowConfirm(
                    $"현재 생성 작업이 진행 중입니다. ({count}개)\n" +
                    "모든 시트를 비우면 Docker 리소스가 일부 생성된 상태로 남을 수 있습니다. 계속하시겠습니까?",
                    "작업 진행 중");
                if (!continueReset)
                    return;
            }

            if (IsModified)
            {
                DialogChoice choice = _dialogService.ShowYesNoCancel(
                    "현재 프로젝트에 저장되지 않은 변경 사항이 있습니다.\n\n" +
                    "예: 저장한 뒤 모든 시트를 비웁니다.\n" +
                    "아니오: 저장하지 않고 모든 시트를 비웁니다.\n" +
                    "취소: 현재 프로젝트로 돌아갑니다.\n\n" +
                    "실제 Docker 및 Kubernetes 리소스는 삭제되지 않습니다.",
                    "모든 시트 비우기");
                if (choice == DialogChoice.Cancel)
                    return;

                if (choice == DialogChoice.Yes && !SaveBeforeProjectReset())
                    return;
            }
            else if (!_dialogService.ShowConfirm(
                         "모든 Docker Engine, Swarm, Kubernetes 시트와 워크스페이스를 비우고 " +
                         "Local PC의 빈 Sheet 1만 남기시겠습니까?\n\n실제 Docker 및 Kubernetes 리소스는 삭제되지 않습니다.",
                         "모든 시트 비우기"))
            {
                return;
            }

            ClearAllWorkspaces();
            AddSheet(markModified: false);
            IsWorkspaceLayer = true;
            CurrentFilePath = null;
            FileService.ClearLastFilePath();
            IsModified = false;
        }

        public bool ClearSheetContents(SheetViewModel? targetSheet)
        {
            if (targetSheet == null || !AllSheets.Contains(targetSheet))
                return false;

            int activeCreationCount = targetSheet.Nodes.Count(node => node.IsBusyCreating);
            string creationWarning = activeCreationCount > 0
                ? $"\n\n이 시트에서 생성 작업 {activeCreationCount}개가 진행 중입니다. " +
                  "화면을 비워도 이미 시작된 런타임 작업은 계속될 수 있습니다."
                : string.Empty;
            bool confirmed = _dialogService.ShowConfirm(
                $"'{targetSheet.Title}' 시트에 표시된 노드, 연결선, 그룹을 모두 지우시겠습니까?" +
                creationWarning +
                "\n\n실제 Docker 및 Kubernetes 리소스는 삭제되지 않습니다.",
                "현재 시트 비우기");
            if (!confirmed)
                return false;

            if (ReferenceEquals(ActiveSheet, targetSheet))
                _mainVm.Inspector?.ClearSelection();

            targetSheet.ClearContents();
            MarkAsModified();
            return true;
        }

        private bool SaveBeforeProjectReset()
        {
            if (!string.IsNullOrWhiteSpace(CurrentFilePath))
            {
                DiagramSaveResult result = FileService.QuickSaveWithResult(_mainVm, CurrentFilePath);
                if (result.Success)
                    return true;

                _dialogService.ShowError(result.GetUserMessage(CurrentFilePath), "저장 실패");
                return false;
            }

            return !string.IsNullOrWhiteSpace(FileService.SaveDiagramAs(_mainVm, _dialogService));
        }

        // --- 5. 이벤트 감시 (Dirty Tracking) ---
        public void MarkAsModified() => IsModified = true;

        private void AttachSheetEvents()
        {
            if (ActiveSheet == null) return;
            ActiveSheet.Nodes.CollectionChanged += Nodes_CollectionChanged;
            ActiveSheet.Groups.CollectionChanged += Groups_CollectionChanged;
            ActiveSheet.Connectors.CollectionChanged += Connectors_CollectionChanged;

            foreach (var node in ActiveSheet.Nodes)
            {
                node.OnModified += Node_OnModified;
                node.OnPositionChanged += Connectable_OnPositionChanged;
            }
            foreach (var group in ActiveSheet.Groups)
            {
                group.OnModified += Node_OnModified;
                group.OnPositionChanged += Connectable_OnPositionChanged;
            }
            foreach (var conn in ActiveSheet.Connectors) conn.OnModified += Connector_OnModified;
        }

        public void UnsubscribeSheetEvents(SheetViewModel sheet)
        {
            if (sheet == null) return;
            sheet.Nodes.CollectionChanged -= Nodes_CollectionChanged;
            sheet.Groups.CollectionChanged -= Groups_CollectionChanged;
            sheet.Connectors.CollectionChanged -= Connectors_CollectionChanged;

            foreach (var node in sheet.Nodes)
            {
                node.OnModified -= Node_OnModified;
                node.OnPositionChanged -= Connectable_OnPositionChanged;
            }
            foreach (var group in sheet.Groups)
            {
                group.OnModified -= Node_OnModified;
                group.OnPositionChanged -= Connectable_OnPositionChanged;
            }
            foreach (var conn in sheet.Connectors) conn.OnModified -= Connector_OnModified;
        }

        private void Nodes_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            var ownerSheet = AllSheets.FirstOrDefault(sheet => ReferenceEquals(sheet.Nodes, sender));
            if (e.NewItems != null)
            {
                foreach (NodeViewModel node in e.NewItems)
                {
                    node.OnModified += Node_OnModified;
                    node.OnPositionChanged += Connectable_OnPositionChanged;
                    if (ownerSheet != null)
                        node.ParentSheet = ownerSheet;
                }
            }

            if (e.OldItems != null)
            {
                foreach (NodeViewModel node in e.OldItems)
                {
                    node.OnModified -= Node_OnModified;
                    node.OnPositionChanged -= Connectable_OnPositionChanged;
                    if (ownerSheet != null && ReferenceEquals(node.ParentSheet, ownerSheet))
                        node.ParentSheet = null;
                    node.Dispose();
                }
            }
            _mainVm.Explorer?.UpdateAvailableItems();
            MarkAsModified();
        }

        private bool ConfirmLeaveActiveCreationTasks(string action)
        {
            int count = ActiveCreationTaskCount;
            return _dialogService.ShowConfirm(
                $"현재 생성 작업이 진행 중입니다. ({count}개)\n{action}\n\n작업 중에 화면을 벗어나면 Docker 리소스가 일부 생성된 상태로 남을 수 있습니다.",
                "작업 진행 중");
        }

        private void Groups_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (GroupViewModel group in e.NewItems)
                {
                    group.OnModified += Node_OnModified;
                    group.OnPositionChanged += Connectable_OnPositionChanged;
                }
            }
            if (e.OldItems != null)
            {
                foreach (GroupViewModel group in e.OldItems)
                {
                    group.OnModified -= Node_OnModified;
                    group.OnPositionChanged -= Connectable_OnPositionChanged;
                }
            }
            _mainVm.Explorer?.UpdateAvailableItems();
            MarkAsModified();
        }

        private void Connectors_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (ConnectorViewModel conn in e.NewItems)
                {
                    conn.OnModified += Connector_OnModified;
                    MarkSwarmTopologyDirty(conn);
                }
            }
            if (e.OldItems != null)
            {
                foreach (ConnectorViewModel conn in e.OldItems)
                {
                    conn.OnModified -= Connector_OnModified;
                    MarkSwarmTopologyDirty(conn);
                }
            }
            MarkAsModified();
        }

        public void MoveSheet(int oldIndex, int newIndex)
        {
            if (oldIndex < 0 || oldIndex >= Sheets.Count || newIndex < 0 || newIndex >= Sheets.Count || oldIndex == newIndex) return;
            Sheets.Move(oldIndex, newIndex);
            MarkAsModified();
        }

        public void RenameSheet(SheetViewModel sheet, string newName)
        {
            if (sheet == null || string.IsNullOrWhiteSpace(newName)) return;

            sheet.Title = newName.Trim();
            MarkAsModified();
        }

        public void RenameWorkspace(ConnectionWorkspaceViewModel workspace, string newName)
        {
            if (workspace == null || string.IsNullOrWhiteSpace(newName)) return;

            string trimmed = newName.Trim();
            workspace.DisplayName = trimmed;
            workspace.Profile.Name = trimmed;
            foreach (var sheet in workspace.Sheets)
            {
                sheet.Profile.Name = trimmed;
            }
            MarkAsModified();
        }

        public bool RemoveWorkspace(ConnectionWorkspaceViewModel workspace)
        {
            if (workspace == null || Workspaces.Count <= 1)
                return false;

            if (workspace.Profile.Type == EndpointType.Local &&
                workspace.RuntimeKind == RuntimeKind.DockerEngine)
                return false;

            foreach (var sheet in workspace.Sheets.ToList())
            {
                DetachSheet(sheet);
            }

            bool serviceUsedElsewhere = Workspaces.Any(w =>
                w != workspace && ReferenceEquals(w.DockerService, workspace.DockerService));

            if (!serviceUsedElsewhere &&
                workspace.Profile.Type == EndpointType.SshRemote &&
                !string.IsNullOrWhiteSpace(workspace.Profile.HostIp))
            {
                SshTunnelManager.ReleaseTunnel(
                    workspace.Profile.HostIp,
                    workspace.Profile.SshPort,
                    workspace.Profile.SshUsername ?? "root",
                    workspace.Profile.RemoteDockerSocketPath
                );
            }

            if (!serviceUsedElsewhere && workspace.DockerService != _defaultDockerService)
            {
                _mainVm.DockerServiceFactory.Release(workspace.DockerService);
            }

            bool wasActive = ActiveWorkspace == workspace;
            Workspaces.Remove(workspace);

            if (wasActive)
            {
                ActiveWorkspace = Workspaces.FirstOrDefault();
                ActiveSheet = ActiveWorkspace?.ActiveSheet ?? ActiveWorkspace?.Sheets.FirstOrDefault();
            }

            OnPropertyChanged(nameof(Sheets));
            MarkAsModified();
            return true;
        }

        public void ClearAllWorkspaces()
        {
            _mainVm.Inspector?.ClearSelection();

            var workspaces = Workspaces.ToList();
            var ownedConnections = workspaces
                .Where(workspace => workspace.DockerService != _defaultDockerService)
                .GroupBy(workspace => workspace.DockerService, ReferenceEqualityComparer.Instance)
                .Select(group => group.First())
                .ToList();

            foreach (var sheet in workspaces.SelectMany(workspace => workspace.Sheets).ToList())
            {
                DetachSheet(sheet);
            }

            Workspaces.Clear();
            _activeWorkspace = null;
            ActiveSheet = null;
            OnPropertyChanged(nameof(ActiveWorkspace));
            OnPropertyChanged(nameof(Sheets));

            foreach (var workspace in ownedConnections)
            {
                ReleaseTunnel(workspace.Profile);
                _mainVm.DockerServiceFactory.Release(workspace.DockerService);
            }
        }

        private void DetachSheet(SheetViewModel sheet)
        {
            UnsubscribeSheetEvents(sheet);
            sheet.RuntimeResourcesChangedAsync = null;

            foreach (NodeViewModel node in sheet.Nodes.ToList())
            {
                if (ReferenceEquals(node.ParentSheet, sheet))
                    node.ParentSheet = null;
            }

            foreach (GroupViewModel group in sheet.Groups.ToList())
            {
                if (ReferenceEquals(group.ParentSheet, sheet))
                    group.ParentSheet = null;
            }

            sheet.Dispose();
        }

        public IDockerService? FindDockerServiceForConnection(ConnectionProfile profile)
        {
            return Workspaces
                .FirstOrDefault(workspace => IsSameConnection(workspace.Profile, profile))
                ?.DockerService;
        }

        private ConnectionWorkspaceViewModel EnsureLocalWorkspace()
        {
            var localProfile = new ConnectionProfile { Name = "Local PC", Type = EndpointType.Local };
            return AddWorkspace(
                localProfile,
                _defaultDockerService,
                activate: true,
                createInitialSheet: false,
                markModified: false);
        }

        private ConnectionWorkspaceViewModel? FindWorkspaceContaining(SheetViewModel sheet)
        {
            return Workspaces.FirstOrDefault(w => w.Sheets.Contains(sheet));
        }

        private ConnectionWorkspaceViewModel? FindWorkspaceByProfile(ConnectionProfile profile)
        {
            return Workspaces.FirstOrDefault(w => IsSameProfile(w.Profile, profile));
        }

        private static bool IsSameProfile(ConnectionProfile left, ConnectionProfile right)
        {
            if (left.Type != right.Type) return false;
            if (left.RuntimeKind != right.RuntimeKind) return false;
            if (left.Type == EndpointType.Local) return true;
            if (left.Type == EndpointType.DockerContext)
            {
                return string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
                    && IsSameDockerContextConnection(left, right);
            }

            return string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.HostIp, right.HostIp, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.SshUsername, right.SshUsername, StringComparison.OrdinalIgnoreCase)
                && left.SshPort == right.SshPort
                && string.Equals(GetRemoteDockerSocketPath(left), GetRemoteDockerSocketPath(right), StringComparison.Ordinal);
        }

        private bool IsServiceUsedByAnyWorkspace(IDockerService dockerService)
        {
            return Workspaces.Any(workspace => ReferenceEquals(workspace.DockerService, dockerService));
        }

        private static string GetRemoteDockerSocketPath(ConnectionProfile profile)
            => string.IsNullOrWhiteSpace(profile.RemoteDockerSocketPath)
                ? SshTunnelManager.DefaultRemoteDockerSocketPath
                : profile.RemoteDockerSocketPath.Trim();

        private static bool IsSameConnection(ConnectionProfile left, ConnectionProfile right)
        {
            if (left.Type != right.Type) return false;

            return left.Type switch
            {
                EndpointType.Local => true,
                EndpointType.SshRemote =>
                    string.Equals(left.HostIp, right.HostIp, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(left.SshUsername ?? "root", right.SshUsername ?? "root", StringComparison.OrdinalIgnoreCase) &&
                    left.SshPort == right.SshPort &&
                    string.Equals(GetRemoteDockerSocketPath(left), GetRemoteDockerSocketPath(right), StringComparison.Ordinal),
                EndpointType.DockerContext =>
                    IsSameDockerContextConnection(left, right),
                _ => false
            };
        }

        private static void DisposeUnownedConnection(ConnectionProfile profile, IDockerService dockerService)
        {
            ReleaseTunnel(profile);
            dockerService.Dispose();
        }

        private static void ReleaseTunnel(ConnectionProfile profile)
        {
            if (profile.Type != EndpointType.SshRemote || string.IsNullOrWhiteSpace(profile.HostIp)) return;

            SshTunnelManager.ReleaseTunnel(
                profile.HostIp,
                profile.SshPort,
                profile.SshUsername ?? "root",
                profile.RemoteDockerSocketPath);
        }

        private static ConnectionProfile CloneProfile(ConnectionProfile source)
        {
            return new ConnectionProfile
            {
                ProfileId = Guid.NewGuid().ToString(),
                Name = source.Name,
                Type = source.Type,
                RuntimeKind = source.RuntimeKind,
                HostIp = source.HostIp,
                SshUsername = source.SshUsername,
                SshPort = source.SshPort,
                LocalTunnelPort = source.LocalTunnelPort,
                SshKeyFilePath = source.SshKeyFilePath,
                RemoteDockerSocketPath = source.RemoteDockerSocketPath,
                DockerContextName = source.DockerContextName,
                DockerEndpoint = source.DockerEndpoint,
                DockerTlsEnabled = source.DockerTlsEnabled,
                DockerTlsSkipVerify = source.DockerTlsSkipVerify,
                DockerTlsCaPath = source.DockerTlsCaPath,
                DockerTlsCertPath = source.DockerTlsCertPath,
                DockerTlsKeyPath = source.DockerTlsKeyPath
            };
        }

        private static bool IsSameDockerContextConnection(ConnectionProfile left, ConnectionProfile right) =>
            string.Equals(left.DockerEndpoint, right.DockerEndpoint, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.DockerTlsCaPath, right.DockerTlsCaPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.DockerTlsCertPath, right.DockerTlsCertPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.DockerTlsKeyPath, right.DockerTlsKeyPath, StringComparison.OrdinalIgnoreCase) &&
            left.DockerTlsEnabled == right.DockerTlsEnabled &&
            left.DockerTlsSkipVerify == right.DockerTlsSkipVerify;

        private static string GetRuntimeLabel(RuntimeKind runtimeKind) => runtimeKind switch
        {
            RuntimeKind.DockerEngine => "Docker",
            RuntimeKind.DockerSwarm => "Swarm",
            RuntimeKind.Kubernetes => "Kubernetes",
            _ => runtimeKind.ToString()
        };

        private void Node_OnModified(object? sender, EventArgs e)
        {
            MarkAsModified();
        }

        private void Connectable_OnPositionChanged(object? sender, EventArgs e)
        {
            if (sender is NodeViewModel { ResourceKind: RuntimeResourceKind.SwarmService } service)
            {
                bool touchesOverlay = service.ParentSheet?.Groups.Any(group =>
                    group.ResourceKind == RuntimeResourceKind.SwarmOverlayNetwork &&
                    (group.ContainedNodes.Contains(service) ||
                     (service.CenterX >= group.X && service.CenterX <= group.X + group.Width &&
                      service.CenterY >= group.Y && service.CenterY <= group.Y + group.Height))) == true;
                if (touchesOverlay)
                    service.IsSwarmDiagramDirty = true;
            }
            else if (sender is GroupViewModel { ResourceKind: RuntimeResourceKind.SwarmOverlayNetwork } group)
            {
                foreach (NodeViewModel containedService in group.ContainedNodes.Where(node =>
                             node.ResourceKind == RuntimeResourceKind.SwarmService))
                {
                    containedService.IsSwarmDiagramDirty = true;
                }
            }
        }

        private void Connector_OnModified(object? sender, EventArgs e)
        {
            if (sender is ConnectorViewModel connector)
                MarkSwarmTopologyDirty(connector);
            MarkAsModified();
        }

        private static void MarkSwarmTopologyDirty(ConnectorViewModel connector)
        {
            if (connector.RelationType is not (RelationType.VolumeMount or
                RelationType.SwarmPublishedPort or
                RelationType.SwarmSecretReference or
                RelationType.SwarmConfigReference))
            {
                return;
            }

            foreach (NodeViewModel service in new[] { connector.Source, connector.Target }
                         .OfType<NodeViewModel>()
                         .Where(node => node.ResourceKind == RuntimeResourceKind.SwarmService))
            {
                service.IsSwarmDiagramDirty = true;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ClearAllWorkspaces();
            GC.SuppressFinalize(this);
        }
    }
}
