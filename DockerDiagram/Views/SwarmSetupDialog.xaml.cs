using DockerDiagram.Contracts;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DockerDiagram.Views
{
    public partial class SwarmSetupDialog : Window
    {
        private readonly ISwarmService _swarmService;
        private readonly IDialogService _dialogService;
        private readonly IDockerServiceFactory _dockerServiceFactory;
        private readonly bool _suggestLocalAddresses;
        private bool _busy;
        private bool _isLoading
        {
            get => _busy;
            set { _busy = value; UpdateActionAvailability(); }
        }
        private bool _targetCanJoin;
        private bool _isDockerDesktopLocalDemo;
        private bool _initializationAddressReady;
        private readonly CancellationTokenSource _lifetimeCts = new();
        private SwarmClusterState? _currentState;
        private SwarmJoinTokens? _joinTokens;
        private SwarmTargetConnectionSession? _targetConnection;

        public SwarmSetupDialog(
            ISwarmService swarmService,
            IDialogService dialogService,
            IDockerServiceFactory dockerServiceFactory,
            bool suggestLocalAddresses = true)
        {
            InitializeComponent();
            _swarmService = swarmService ?? throw new ArgumentNullException(nameof(swarmService));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            _dockerServiceFactory = dockerServiceFactory ?? throw new ArgumentNullException(nameof(dockerServiceFactory));
            _suggestLocalAddresses = suggestLocalAddresses;

            ApplyInitializationDefaults(suggestLocalAddresses
                ? new SwarmInitializationDefaults(
                    Array.Empty<string>(),
                    string.Empty,
                    "0.0.0.0:2377",
                    IsDockerDesktopLocalDemo: false)
                : SwarmInitializationDefaults.Create(
                    suggestLocalAddresses: false,
                    osType: null,
                    operatingSystem: null));

            // 진행 중인 요청의 연결이 중간에 해제되지 않게 합니다.
            Closing += (_, e) => e.Cancel = _isLoading;
            Closed += (_, _) =>
            {
                _lifetimeCts.Cancel();
                ClearJoinSecrets();
                ClearTargetConnection();
            };
        }

        public bool WasInitialized { get; private set; }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await RefreshInitializationDefaultsAsync();
            await RefreshStateAsync();
        }

        private async Task RefreshInitializationDefaultsAsync()
        {
            if (!_suggestLocalAddresses || _swarmService is not IContainerService containerService)
                return;

            try
            {
                var systemInfo = await containerService.GetSystemInfoAsync();
                IReadOnlyList<string>? daemonCandidates = null;
                bool dockerDesktopLinux =
                    string.Equals(systemInfo.OSType?.Trim(), "linux", StringComparison.OrdinalIgnoreCase) &&
                    systemInfo.OperatingSystem?.Contains("Docker Desktop", StringComparison.OrdinalIgnoreCase) == true;
                if (dockerDesktopLinux && _swarmService is INetworkService networkService)
                {
                    try
                    {
                        var bridgeNetwork = await networkService.InspectNetworkAsync("bridge");
                        daemonCandidates = SwarmAdvertiseAddressDiscovery.GetDaemonBridgeIpv4Candidates(bridgeNetwork);
                    }
                    catch
                    {
                        daemonCandidates = Array.Empty<string>();
                    }
                }

                ApplyInitializationDefaults(SwarmInitializationDefaults.Create(
                    suggestLocalAddresses: true,
                    systemInfo.OSType,
                    systemInfo.OperatingSystem,
                    daemonCandidates: daemonCandidates));
            }
            catch
            {
                ApplyInitializationDefaults(new SwarmInitializationDefaults(
                    Array.Empty<string>(),
                    string.Empty,
                    "0.0.0.0:2377",
                    IsDockerDesktopLocalDemo: false));
                InitializationGuidanceText.Text =
                    "Docker Engine 환경과 주소를 확인하지 못했습니다. 잘못된 호스트 주소로 초기화하지 않도록 작업을 중지했습니다.";
            }
        }

        private void ApplyInitializationDefaults(SwarmInitializationDefaults defaults)
        {
            _isDockerDesktopLocalDemo = defaults.IsDockerDesktopLocalDemo;
            _initializationAddressReady = !_suggestLocalAddresses ||
                                          !string.IsNullOrWhiteSpace(defaults.AdvertiseAddress);
            AdvertiseAddressBox.ItemsSource = defaults.AdvertiseCandidates;
            AdvertiseAddressBox.Text = defaults.AdvertiseAddress;
            ListenAddressTextBox.Text = defaults.ListenAddress;
            InitializationGuidanceText.Text = defaults.IsDockerDesktopLocalDemo
                ? defaults.AdvertiseAddress.Length == 0
                    ? "Docker Desktop의 Linux VM을 감지했지만 dockerd가 소유한 IPv4를 찾지 못했습니다. 이 상태에서는 초기화하지 말고 Docker Desktop 네트워크 설정을 확인하세요."
                    : $"Docker Desktop의 daemon 주소 {defaults.AdvertiseAddress}를 사용합니다. 이 설정은 한 노드 로컬 데모 전용이며 다른 PC는 가입시킬 수 없습니다."
                : "여러 PC를 연결하려면 다른 노드에서 접근 가능한 LAN 주소를 선택하세요. 자동 후보가 맞지 않으면 직접 입력할 수 있습니다.";
            AddTargetNodeButton.ToolTip = defaults.IsDockerDesktopLocalDemo
                ? "Docker Desktop 로컬 Swarm은 외부 노드 가입용 Manager로 사용하지 않습니다. 원격 Linux Docker Engine을 Manager로 초기화하세요."
                : "SSH로 대상 Docker Engine과 Swarm 미가입 상태를 먼저 검증합니다.";
            UpdateActionAvailability();
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await RefreshStateAsync();
        }

        private async void InitializeSwarm_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;

            if (!uint.TryParse(DataPathPortTextBox.Text.Trim(), out uint dataPathPort))
            {
                _dialogService.ShowError("Data Path Port는 숫자로 입력해 주세요.", "Swarm 초기화");
                return;
            }

            var options = new SwarmInitializeOptions
            {
                AdvertiseAddress = AdvertiseAddressBox.Text.Trim(),
                ListenAddress = ListenAddressTextBox.Text.Trim(),
                DataPathPort = dataPathPort,
                AutoLockManagers = false
            };

            try
            {
                if (!_initializationAddressReady)
                    throw new InvalidOperationException("Docker Engine이 실제로 사용할 수 있는 초기화 주소를 확인하지 못했습니다.");

                options.Validate();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(ex.Message, "Swarm 초기화");
                return;
            }

            string portSummary = dataPathPort == 0 ? "Docker 기본값" : dataPathPort.ToString();
            bool confirmed = _dialogService.ShowConfirm(
                "이 Docker Engine을 새 Swarm의 Manager로 초기화합니다.\n\n" +
                $"Advertise Address: {options.AdvertiseAddress}\n" +
                $"Listen Address: {options.ListenAddress}\n" +
                $"Data Path Port: {portSummary}\n\n" +
                "다른 PC를 연결하려면 방화벽에서 TCP 2377, TCP/UDP 7946, UDP 4789(또는 지정 포트)가 통신 가능해야 합니다.\n\n" +
                "계속하시겠습니까?",
                "새 Swarm 초기화");
            if (!confirmed) return;

            _isLoading = true;
            InitializeButton.IsEnabled = false;
            LoadingText.Text = "Swarm Manager 초기화 중...";

            try
            {
                using var initCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                initCts.CancelAfter(TimeSpan.FromSeconds(30));
                string nodeId = await _swarmService.InitializeSwarmAsync(options, initCts.Token);
                SwarmClusterState confirmedState = await WaitForManagerStateAsync(_lifetimeCts.Token);

                WasInitialized = true;
                _dialogService.ShowInfo(
                    "Swarm Manager 초기화가 완료되었습니다.\n" +
                    $"Node ID: {EmptyAsDash(confirmedState.NodeId.Length > 0 ? confirmedState.NodeId : nodeId)}\n" +
                    $"Advertise Address: {EmptyAsDash(confirmedState.NodeAddress.Length > 0 ? confirmedState.NodeAddress : options.AdvertiseAddress)}",
                    "Swarm 초기화 완료");
                _isLoading = false;
                DialogResult = true;
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                // 창이 닫히면서 취소된 경우 별도 오류창을 표시하지 않습니다.
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(
                    $"Swarm 초기화 실패:\n{ex.GetBaseException().Message}",
                    "Swarm 초기화");
            }
            finally
            {
                _isLoading = false;
                LoadingText.Text = string.Empty;
                InitializeButton.IsEnabled = true;
            }

            if (!WasInitialized)
                await RefreshStateAsync();
        }

        private async void ShowJoinInfo_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading || _currentState?.IsManager != true) return;

            _isLoading = true;
            JoinInfoButton.IsEnabled = false;
            LoadingText.Text = "Join Token 조회 중...";

            try
            {
                using var tokenCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                tokenCts.CancelAfter(TimeSpan.FromSeconds(15));
                _joinTokens = await _swarmService.GetJoinTokensAsync(tokenCts.Token);
                JoinManagerAddressTextBox.Text = GetDefaultManagerAddress(_currentState);
                JoinInfoPanel.Visibility = Visibility.Visible;
                UpdateJoinInfoPresentation();
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                ClearJoinSecrets();
                _dialogService.ShowError(
                    $"Join 정보 조회 실패:\n{SafeJoinError(ex, string.Empty)}",
                    "Swarm Join 정보");
            }
            finally
            {
                _isLoading = false;
                JoinInfoButton.IsEnabled = _currentState?.IsManager == true;
                LoadingText.Text = string.Empty;
            }
        }

        private void JoinRole_Changed(object sender, RoutedEventArgs e) =>
            UpdateJoinInfoPresentation();

        private void RevealJoinToken_Changed(object sender, RoutedEventArgs e) =>
            UpdateJoinInfoPresentation();

        private void JoinManagerAddress_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
            UpdateJoinInfoPresentation();

        private void CopyJoinToken_Click(object sender, RoutedEventArgs e)
        {
            string token = GetSelectedJoinToken();
            if (string.IsNullOrWhiteSpace(token)) return;

            try
            {
                _dialogService.SetClipboardText(token);
                _dialogService.ShowInfo("선택한 역할의 Join Token을 클립보드에 복사했습니다.", "Swarm Join 정보");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Token 복사 실패:\n{ex.Message}", "Swarm Join 정보");
            }
        }

        private void CopyJoinCommand_Click(object sender, RoutedEventArgs e)
        {
            string token = GetSelectedJoinToken();
            if (string.IsNullOrWhiteSpace(token)) return;

            try
            {
                string command = SwarmJoinCommandBuilder.Build(token, JoinManagerAddressTextBox.Text);
                _dialogService.SetClipboardText(command);
                _dialogService.ShowInfo("Join 명령을 클립보드에 복사했습니다.", "Swarm Join 정보");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(ex.Message, "Swarm Join 정보");
            }
        }

        private void ShowCurrentManagerInfo_Click(object sender, RoutedEventArgs e)
        {
            if (_currentState?.IsWorker != true) return;

            string managers = _currentState.RemoteManagers.Count == 0
                ? "Docker가 현재 Manager 주소를 반환하지 않았습니다."
                : string.Join(Environment.NewLine, _currentState.RemoteManagers.Select(manager =>
                    string.IsNullOrWhiteSpace(manager.NodeId)
                        ? manager.Address
                        : $"{manager.Address} · {manager.NodeId}"));
            _dialogService.ShowInfo(managers, "현재 Swarm Manager");
        }

        private void AddTargetNode_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading || _currentState?.IsManager != true) return;

            var dialog = new SwarmTargetConnectionDialog(_dockerServiceFactory, _dialogService)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true || dialog.ConnectionSession == null) return;

            ClearTargetConnection();
            _targetConnection = dialog.ConnectionSession;
            _targetCanJoin = true;
            SwarmTargetConnectionOptions options = _targetConnection.Options;
            TargetConnectionSummaryText.Text =
                $"{options.Role} · {options.Host}:{options.SshPort}\n" +
                $"Advertise Address: {options.AdvertiseAddress}\n" +
                "Docker 응답 확인 · Swarm 미가입 확인 · Join 미실행";
            TargetManagerAddressTextBox.Text = GetDefaultManagerAddress(_currentState);
            DisconnectTargetButton.Visibility = Visibility.Visible;
            JoinTargetButton.Content = "Swarm Join 실행";
            JoinTargetButton.IsEnabled = true;
            TargetConnectionPanel.Visibility = Visibility.Visible;
            UpdateActionAvailability();
        }

        private void DisconnectTarget_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            ClearTargetConnection();
            _dialogService.ShowInfo("대상 PC의 임시 Docker 연결과 SSH 터널을 해제했습니다.", "대상 PC 연결");
        }

        private async void JoinTarget_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading || !_targetCanJoin || _currentState?.IsManager != true || _targetConnection == null) return;

            SwarmTargetConnectionSession connection = _targetConnection;
            SwarmTargetConnectionOptions target = connection.Options;
            string managerAddress;
            try
            {
                managerAddress = SwarmJoinCommandBuilder.NormalizeManagerAddress(TargetManagerAddressTextBox.Text);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(ex.Message, "Swarm Join");
                return;
            }

            bool confirmed = _dialogService.ShowConfirm(
                "검증된 원격 Docker Engine을 현재 Swarm에 가입시킵니다.\n\n" +
                $"대상 PC: {target.Host}\n" +
                $"역할: {target.Role}\n" +
                $"Advertise Address: {target.AdvertiseAddress}\n" +
                $"Manager Endpoint: {managerAddress}\n\n" +
                "대상 PC에서 Manager Endpoint로 접근할 수 있어야 하며 TCP 2377, TCP/UDP 7946, UDP 4789 통신이 필요합니다.\n\n" +
                "계속하시겠습니까?",
                "원격 PC Swarm Join");
            if (!confirmed) return;

            _targetCanJoin = false;
            _isLoading = true;
            AddTargetNodeButton.IsEnabled = false;
            JoinInfoButton.IsEnabled = false;
            JoinTargetButton.IsEnabled = false;
            DisconnectTargetButton.IsEnabled = false;
            LoadingText.Text = "Join Token 조회 중...";
            string activeToken = string.Empty;

            try
            {
                var freshState = await SwarmLeaveWorkflow.ReadStateAsync(connection.DockerService, _lifetimeCts.Token);
                if (freshState.Membership != SwarmMembershipState.Inactive)
                    throw new InvalidOperationException("대상 PC의 가입 상태가 변경되었습니다.");
                using var tokenCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                tokenCts.CancelAfter(TimeSpan.FromSeconds(15));
                SwarmJoinTokens tokens = await _swarmService.GetJoinTokensAsync(tokenCts.Token);
                string token = tokens.GetToken(target.Role);
                activeToken = token;
                var joinOptions = new SwarmJoinOptions
                {
                    RemoteManagerAddresses = new[] { managerAddress },
                    JoinToken = token,
                    Role = target.Role,
                    ListenAddress = "0.0.0.0:2377",
                    AdvertiseAddress = target.AdvertiseAddress,
                    Availability = "active"
                };
                joinOptions.Validate();

                LoadingText.Text = $"{target.Host}에 Swarm Join 요청 중...";
                using var joinCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                joinCts.CancelAfter(TimeSpan.FromSeconds(30));
                await connection.DockerService.JoinSwarmAsync(joinOptions, joinCts.Token);

                LoadingText.Text = "대상 노드 역할 확인 중...";
                SwarmClusterState joinedState = await WaitForTargetJoinedStateAsync(
                    connection.DockerService,
                    target.Role,
                    _lifetimeCts.Token);

                LoadingText.Text = "Manager에서 새 노드 확인 중...";
                DockerSwarmNode? managerNode = await WaitForManagerNodeAsync(
                    joinedState.NodeId,
                    target.AdvertiseAddress,
                    target.Role,
                    _lifetimeCts.Token);

                if (managerNode != null)
                    CompleteTargetJoin(target, joinedState, managerNode);
                else
                    PresentTargetRecovery(joinedState, "대상 역할은 확인했지만 현재 Manager의 노드 목록 반영은 확인 대기 중입니다.");
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                // 토큰이나 응답 유실을 성공으로 오인하지 않고 양쪽 엔진을 재조회합니다.
                await RecoverTargetAsync(SafeJoinError(ex, activeToken));
            }
            finally
            {
                ClearJoinSecrets();
                activeToken = string.Empty;
                _isLoading = false;
                LoadingText.Text = string.Empty;
                UpdateActionAvailability();
            }
        }

        private async Task RefreshStateAsync()
        {
            if (_isLoading) return;

            _isLoading = true;
            LoadingText.Text = "Swarm 상태 확인 중...";
            InitializeButton.IsEnabled = false;
            ClearJoinSecrets();
            SetActionPanels(SwarmSetupPanelKind.Error, visible: false);

            try
            {
                SwarmClusterState state = await SwarmLeaveWorkflow.ReadStateAsync(_swarmService, _lifetimeCts.Token);
                _currentState = state;
                if (!state.IsManager)
                    ClearTargetConnection();
                SwarmSetupPresentation presentation = SwarmSetupPresentation.FromState(state);

                StatusTitleText.Text = presentation.StatusTitle;
                StatusDescriptionText.Text = presentation.StatusDescription;
                NodeIdText.Text = EmptyAsDash(state.NodeId);
                NodeAddressText.Text = EmptyAsDash(state.NodeAddress);
                ManagersText.Text = state.RemoteManagers.Count == 0
                    ? "-"
                    : string.Join(Environment.NewLine, state.RemoteManagers.Select(manager =>
                        string.IsNullOrWhiteSpace(manager.NodeId)
                            ? manager.Address
                            : $"{manager.Address} · {manager.NodeId}"));

                ErrorText.Text = presentation.StatusDescription;
                SetActionPanels(presentation.Panel, visible: true);
            }
            catch (Exception ex)
            {
                _currentState = null;
                StatusTitleText.Text = "Swarm 상태 확인 실패";
                StatusDescriptionText.Text = "Docker Engine에서 Swarm 상태를 읽지 못했습니다.";
                NodeIdText.Text = "-";
                NodeAddressText.Text = "-";
                ManagersText.Text = "-";
                ErrorText.Text = ex.GetBaseException().Message;
                SetActionPanels(SwarmSetupPanelKind.Error, visible: true);
            }
            finally
            {
                _isLoading = false;
                LoadingText.Text = string.Empty;
                InitializeButton.IsEnabled = InactiveActionsPanel.Visibility == Visibility.Visible;
            }
        }

        private async Task<SwarmClusterState> WaitForManagerStateAsync(CancellationToken cancellationToken)
        {
            SwarmClusterState latest = await SwarmLeaveWorkflow.ReadStateAsync(_swarmService, cancellationToken);
            for (int attempt = 0; attempt < 12 && !latest.IsManager; attempt++)
            {
                await Task.Delay(250, cancellationToken);
                latest = await SwarmLeaveWorkflow.ReadStateAsync(_swarmService, cancellationToken);
            }

            if (!latest.IsManager)
            {
                throw new InvalidOperationException(
                    $"Docker가 초기화 요청을 처리했지만 Manager 상태를 확인하지 못했습니다. 현재 상태: {latest.Membership}");
            }

            return latest;
        }

        private static async Task<SwarmClusterState> WaitForTargetJoinedStateAsync(
            ISwarmService targetService,
            SwarmJoinRole role,
            CancellationToken cancellationToken)
        {
            SwarmClusterState latest = await SwarmLeaveWorkflow.ReadStateAsync(targetService, cancellationToken);
            for (int attempt = 0; attempt < 20 && !SwarmJoinVerification.HasExpectedRole(latest, role); attempt++)
            {
                await Task.Delay(250, cancellationToken);
                latest = await SwarmLeaveWorkflow.ReadStateAsync(targetService, cancellationToken);
            }

            if (!SwarmJoinVerification.HasExpectedRole(latest, role))
                throw new InvalidOperationException($"Join 요청 후 대상 역할을 확인하지 못했습니다. 현재 상태: {latest.Membership}");
            return latest;
        }

        private async Task<DockerSwarmNode?> WaitForManagerNodeAsync(
            string nodeId,
            string advertiseAddress,
            SwarmJoinRole role,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(nodeId)) return null;
            DockerSwarmNode? match = null;
            for (int attempt = 0; attempt < 20 && match == null; attempt++)
            {
                IReadOnlyList<DockerSwarmNode> nodes = await _swarmService.GetSwarmNodesAsync()
                    .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                match = SwarmJoinVerification.FindJoinedNode(nodes, nodeId, advertiseAddress, role);
                if (match == null) await Task.Delay(250, cancellationToken);
            }
            return match;
        }

        private static async Task<SwarmClusterState?> TryReadTargetStateAsync(ISwarmService targetService)
        {
            try { return await SwarmLeaveWorkflow.ReadStateAsync(targetService); }
            catch { return null; }
        }

        private void CompleteTargetJoin(
            SwarmTargetConnectionOptions target,
            SwarmClusterState joinedState,
            DockerSwarmNode? managerNode)
        {
            ClearTargetConnection();
            TargetConnectionSummaryText.Text =
                $"Join 완료 · {target.Role} · {target.Host}\n" +
                $"Node ID: {EmptyAsDash(joinedState.NodeId)}\n" +
                $"Advertise Address: {target.AdvertiseAddress}\n" +
                (managerNode == null
                    ? "대상 상태 확인 완료 · Manager 목록 동기화 대기"
                    : $"Manager 확인 완료 · {managerNode.Hostname} · {managerNode.Status}");
            TargetManagerAddressTextBox.Text = string.Empty;
            TargetManagerAddressTextBox.IsEnabled = false;
            DisconnectTargetButton.Visibility = Visibility.Collapsed;
            JoinTargetButton.Content = "Join 완료";
            JoinTargetButton.IsEnabled = false;
            TargetConnectionPanel.Visibility = Visibility.Visible;

            _dialogService.ShowInfo(
                $"원격 PC가 Swarm {target.Role}로 가입되었습니다.\n\nNode ID: {EmptyAsDash(joinedState.NodeId)}" +
                (managerNode == null ? "\nManager 목록 반영: 확인 대기" : $"\nManager에서 확인: {managerNode.Hostname}"),
                "Swarm Join 완료");
        }

        private void UpdateJoinInfoPresentation()
        {
            if (_joinTokens == null ||
                JoinTokenPasswordBox == null ||
                JoinTokenVisibleTextBox == null ||
                JoinCommandTextBox == null ||
                JoinManagerAddressTextBox == null ||
                ManagerAddressErrorText == null)
            {
                return;
            }

            string token = GetSelectedJoinToken();
            JoinTokenPasswordBox.Password = token;
            JoinTokenVisibleTextBox.Text = token;

            bool reveal = RevealJoinTokenCheckBox?.IsChecked == true;
            JoinTokenPasswordBox.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
            JoinTokenVisibleTextBox.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;

            try
            {
                JoinCommandTextBox.Text = reveal
                    ? SwarmJoinCommandBuilder.Build(token, JoinManagerAddressTextBox.Text)
                    : SwarmJoinCommandBuilder.BuildRedacted(JoinManagerAddressTextBox.Text);
                ManagerAddressErrorText.Text = string.Empty;
            }
            catch (Exception ex)
            {
                JoinCommandTextBox.Text = string.Empty;
                ManagerAddressErrorText.Text = ex.Message;
            }
        }

        private string GetSelectedJoinToken()
        {
            if (_joinTokens == null) return string.Empty;
            return ManagerRoleRadio?.IsChecked == true
                ? _joinTokens.ManagerToken
                : _joinTokens.WorkerToken;
        }

        private static string GetDefaultManagerAddress(SwarmClusterState state)
        {
            // NodeAddr에는 포트가 없으므로 현재 Manager의 실제 endpoint를 우선합니다.
            string address = state.RemoteManagers.FirstOrDefault(manager =>
                !string.IsNullOrWhiteSpace(state.NodeId) && manager.NodeId == state.NodeId)?.Address ?? string.Empty;
            if (string.IsNullOrWhiteSpace(address))
                address = !string.IsNullOrWhiteSpace(state.NodeAddress)
                    ? state.NodeAddress
                    : state.RemoteManagers.FirstOrDefault()?.Address ?? string.Empty;
            return string.IsNullOrWhiteSpace(address)
                ? string.Empty
                : SwarmJoinCommandBuilder.NormalizeManagerAddress(address);
        }

        private void ClearJoinSecrets()
        {
            _joinTokens = null;
            if (JoinTokenPasswordBox != null) JoinTokenPasswordBox.Clear();
            if (JoinTokenVisibleTextBox != null) JoinTokenVisibleTextBox.Clear();
            if (JoinCommandTextBox != null) JoinCommandTextBox.Clear();
            if (JoinInfoPanel != null) JoinInfoPanel.Visibility = Visibility.Collapsed;
            if (RevealJoinTokenCheckBox != null) RevealJoinTokenCheckBox.IsChecked = false;
        }

        private void SetActionPanels(SwarmSetupPanelKind panel, bool visible)
        {
            InactiveActionsPanel.Visibility = visible && panel == SwarmSetupPanelKind.Inactive
                ? Visibility.Visible : Visibility.Collapsed;
            ManagerActionsPanel.Visibility = visible && panel == SwarmSetupPanelKind.Manager
                ? Visibility.Visible : Visibility.Collapsed;
            WorkerActionsPanel.Visibility = visible && panel == SwarmSetupPanelKind.Worker
                ? Visibility.Visible : Visibility.Collapsed;
            WaitingPanel.Visibility = visible && panel == SwarmSetupPanelKind.Waiting
                ? Visibility.Visible : Visibility.Collapsed;
            ErrorPanel.Visibility = visible && panel == SwarmSetupPanelKind.Error
                ? Visibility.Visible : Visibility.Collapsed;
            InitializeButton.IsEnabled =
                visible && panel == SwarmSetupPanelKind.Inactive && !_isLoading && _initializationAddressReady;
            JoinInfoButton.IsEnabled =
                visible && panel == SwarmSetupPanelKind.Manager && !_isLoading;
            AddTargetNodeButton.IsEnabled =
                visible && panel == SwarmSetupPanelKind.Manager && !_isLoading && !_isDockerDesktopLocalDemo;
            WorkerManagerInfoButton.IsEnabled =
                visible && panel == SwarmSetupPanelKind.Worker && !_isLoading;
            JoinTargetButton.IsEnabled =
                visible && panel == SwarmSetupPanelKind.Manager && !_isLoading &&
                !_isDockerDesktopLocalDemo && _targetConnection != null;
        }

        private static string EmptyAsDash(string value) =>
            string.IsNullOrWhiteSpace(value) ? "-" : value;

        private void ClearTargetConnection()
        {
            var connection = _targetConnection;
            _targetConnection = null;
            _targetCanJoin = false;
            connection?.Dispose();
            if (TargetConnectionSummaryText != null) TargetConnectionSummaryText.Text = string.Empty;
            if (TargetManagerAddressTextBox != null)
            {
                TargetManagerAddressTextBox.Text = string.Empty;
                TargetManagerAddressTextBox.IsEnabled = true;
            }
            if (DisconnectTargetButton != null)
            {
                DisconnectTargetButton.Visibility = Visibility.Visible;
                DisconnectTargetButton.IsEnabled = true;
            }
            if (JoinTargetButton != null)
            {
                JoinTargetButton.Content = "Swarm Join 실행";
                JoinTargetButton.IsEnabled = false;
            }
            if (TargetConnectionPanel != null) TargetConnectionPanel.Visibility = Visibility.Collapsed;
        }
    }
}
