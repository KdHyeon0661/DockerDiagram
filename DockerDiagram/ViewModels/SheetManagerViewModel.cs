using DockerDiagram.ApplicationServices;
using DockerDiagram.Infrastructure;
using DockerDiagram.Contracts;
using DockerDiagram.Common;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
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
        private readonly ObservableCollection<SheetViewModel> _emptySheets = new();

        // --- 1. 상태 및 데이터 ---
        public ObservableCollection<ConnectionWorkspaceViewModel> Workspaces { get; } = new();

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

        private bool _isModified = false;
        public bool IsModified { get => _isModified; set => SetProperty(ref _isModified, value); }

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
        public ICommand DeleteAllSheetCommand { get; }
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
            DeleteAllSheetCommand = new RelayCommand(ExecuteDeleteAllSheet);

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
                        await RestoreLiveStateAsync();
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[Load Error]: {ex.Message}"); }
        }

        public async Task RestoreLiveStateAsync()
        {
            foreach (var sheet in AllSheets)
            {
                foreach (var node in sheet.Nodes)
                {
                    node.ParentSheet = sheet;
                    await node.RefreshDetailsAsync();
                }
                foreach (var group in sheet.Groups)
                {
                    group.ParentSheet = sheet;
                    if (group.ContainedNodes != null)
                    {
                        foreach (var node in group.ContainedNodes)
                        {
                            node.ParentSheet = sheet;
                            await node.RefreshDetailsAsync();
                        }
                    }
                }
            }
        }

        // --- 4. 탭(Sheet) 관리 로직 ---
        public void AddSheet()
        {
            var workspace = ActiveWorkspace ?? EnsureLocalWorkspace();
            AddSheetToWorkspace(workspace, $"Sheet {workspace.Sheets.Count + 1}", activate: true);
        }

        public ConnectionWorkspaceViewModel AddWorkspace(ConnectionProfile profile, IDockerService dockerService, bool activate = true, bool createInitialSheet = true)
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
                createInitialSheet: false);

            if (workspace.Sheets.Count == 0)
            {
                AddSheetToWorkspace(workspace, $"{GetRuntimeLabel(runtimeKind)} Sheet 1", activate: false);
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

            workspace.Sheets.Remove(sheet);
            OnPropertyChanged(nameof(Sheets));
        }

        private void NavigateSheet(int direction)
        {
            if (ActiveSheet == null || Sheets.Count <= 1) return;
            int currentIndex = Sheets.IndexOf(ActiveSheet);
            int newIndex = currentIndex + direction;
            if (newIndex >= 0 && newIndex < Sheets.Count) ActiveSheet = Sheets[newIndex];
        }

        private void ExecuteDeleteAllSheet(object? obj)
        {
            if (_dialogService.ShowConfirm("모든 시트를 삭제하시겠습니까?", "Delete All Sheet"))
            {
                ClearAllWorkspaces();
                AddSheet();
            }
        }

        // --- 5. 이벤트 감시 (Dirty Tracking) ---
        public void MarkAsModified() => IsModified = true;

        private void AttachSheetEvents()
        {
            if (ActiveSheet == null) return;
            ActiveSheet.Nodes.CollectionChanged += Nodes_CollectionChanged;
            ActiveSheet.Groups.CollectionChanged += Groups_CollectionChanged;
            ActiveSheet.Connectors.CollectionChanged += Connectors_CollectionChanged;

            foreach (var node in ActiveSheet.Nodes) node.OnModified += Node_OnModified;
            foreach (var group in ActiveSheet.Groups) group.OnModified += Node_OnModified;
            foreach (var conn in ActiveSheet.Connectors) conn.OnModified += Connector_OnModified;
        }

        public void UnsubscribeSheetEvents(SheetViewModel sheet)
        {
            if (sheet == null) return;
            sheet.Nodes.CollectionChanged -= Nodes_CollectionChanged;
            sheet.Groups.CollectionChanged -= Groups_CollectionChanged;
            sheet.Connectors.CollectionChanged -= Connectors_CollectionChanged;

            foreach (var node in sheet.Nodes) node.OnModified -= Node_OnModified;
            foreach (var group in sheet.Groups) group.OnModified -= Node_OnModified;
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
                    if (ownerSheet != null)
                        node.ParentSheet = ownerSheet;
                }
            }

            if (e.OldItems != null)
            {
                foreach (NodeViewModel node in e.OldItems)
                {
                    node.OnModified -= Node_OnModified;
                    if (ownerSheet != null && ReferenceEquals(node.ParentSheet, ownerSheet))
                        node.ParentSheet = null;
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
            if (e.NewItems != null) foreach (GroupViewModel group in e.NewItems) group.OnModified += Node_OnModified;
            if (e.OldItems != null) foreach (GroupViewModel group in e.OldItems) group.OnModified -= Node_OnModified;
            _mainVm.Explorer?.UpdateAvailableItems();
            MarkAsModified();
        }

        private void Connectors_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null) foreach (ConnectorViewModel conn in e.NewItems) conn.OnModified += Connector_OnModified;
            if (e.OldItems != null) foreach (ConnectorViewModel conn in e.OldItems) conn.OnModified -= Connector_OnModified;
            MarkAsModified();
        }

        public void MoveSheet(int oldIndex, int newIndex)
        {
            if (oldIndex < 0 || oldIndex >= Sheets.Count || newIndex < 0 || newIndex >= Sheets.Count) return;
            Sheets.Move(oldIndex, newIndex);
        }

        public void RenameSheet(SheetViewModel sheet, string newName)
        {
            if (sheet != null && !string.IsNullOrWhiteSpace(newName)) sheet.Title = newName;
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
                UnsubscribeSheetEvents(sheet);
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
            var workspaces = Workspaces.ToList();
            var ownedConnections = workspaces
                .Where(workspace => workspace.DockerService != _defaultDockerService)
                .GroupBy(workspace => workspace.DockerService, ReferenceEqualityComparer.Instance)
                .Select(group => group.First())
                .ToList();

            foreach (var sheet in workspaces.SelectMany(workspace => workspace.Sheets).ToList())
            {
                UnsubscribeSheetEvents(sheet);
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

        public IDockerService? FindDockerServiceForConnection(ConnectionProfile profile)
        {
            return Workspaces
                .FirstOrDefault(workspace => IsSameConnection(workspace.Profile, profile))
                ?.DockerService;
        }

        private ConnectionWorkspaceViewModel EnsureLocalWorkspace()
        {
            var localProfile = new ConnectionProfile { Name = "Local PC", Type = EndpointType.Local };
            return AddWorkspace(localProfile, _defaultDockerService, activate: true, createInitialSheet: false);
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
                    && string.Equals(left.DockerEndpoint, right.DockerEndpoint, StringComparison.OrdinalIgnoreCase);
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
                    string.Equals(left.DockerEndpoint, right.DockerEndpoint, StringComparison.OrdinalIgnoreCase),
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
                DockerEndpoint = source.DockerEndpoint
            };
        }

        private static string GetRuntimeLabel(RuntimeKind runtimeKind) => runtimeKind switch
        {
            RuntimeKind.DockerEngine => "Docker",
            RuntimeKind.DockerSwarm => "Swarm",
            RuntimeKind.Kubernetes => "Kubernetes",
            _ => runtimeKind.ToString()
        };

        private void Node_OnModified(object? sender, EventArgs e) => MarkAsModified();
        private void Connector_OnModified(object? sender, EventArgs e) => MarkAsModified();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ClearAllWorkspaces();
        }
    }
}
