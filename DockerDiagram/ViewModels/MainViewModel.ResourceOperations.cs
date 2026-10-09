using DockerDiagram.Diagram;
using Docker.DotNet.Models;
using DockerDiagram.Models;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Contracts;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DockerDiagram.ViewModels
{
    public partial class MainViewModel
    {
        // =========================================================
        public async Task AddConnectionAsync(IConnectableItem source, IConnectableItem target, PortDirection sourceDir, PortDirection targetDir)
        {
            if (ActiveSheet == null || source == target) return;
            SheetViewModel sheet = ActiveSheet;

            RelationType? swarmRelation = null;
            bool reverseForSemanticDirection = false;
            if (sheet.RuntimeKind == RuntimeKind.DockerSwarm)
            {
                SwarmConnectionDecision decision = SwarmConnectionPolicy.Resolve(
                    GetEffectiveSwarmKind(source),
                    GetEffectiveSwarmKind(target));
                if (!decision.IsAllowed)
                {
                    _dialogService.ShowMessage(decision.ErrorMessage);
                    return;
                }

                swarmRelation = decision.RelationType;
                reverseForSemanticDirection = decision.ReverseDirection;
            }
            else if (!IsValidConnection(source, target))
            {
                _dialogService.ShowMessage("연결할 수 없는 조합입니다.\n(볼륨끼리 연결하거나, 인터넷과 볼륨은 연결할 수 없습니다.)");
                return;
            }

            IConnectableItem finalSource = source;
            IConnectableItem finalTarget = target;
            PortDirection finalSourceDir = sourceDir;
            PortDirection finalTargetDir = targetDir;

            if (reverseForSemanticDirection)
            {
                (finalSource, finalTarget) = (finalTarget, finalSource);
                (finalSourceDir, finalTargetDir) = (finalTargetDir, finalSourceDir);
            }

            NodeViewModel? volumeContainer = new[] { finalSource, finalTarget }
                .OfType<NodeViewModel>()
                .FirstOrDefault(node => node.Type == NodeType.Container);
            NodeViewModel? volumeNode = new[] { finalSource, finalTarget }
                .OfType<NodeViewModel>()
                .FirstOrDefault(node => node.Type == NodeType.Volume);
            bool isVolumeMount = swarmRelation == RelationType.VolumeMount ||
                                 (swarmRelation == null && volumeContainer != null && volumeNode != null);
            bool isDraftConnection = new[] { finalSource, finalTarget }
                .OfType<ConnectableItemViewModel>()
                .Any(item => item.IsDraft);

            // 볼륨 마운트는 연결 방향과 무관하게 양 끝의 리소스 역할로 처리합니다.
            if (isVolumeMount && sheet.RuntimeKind != RuntimeKind.DockerSwarm && !isDraftConnection)
            {
                bool isSuccess = await ConnectVolumeToContainerAsync(volumeContainer!, volumeNode!);
                if (!isSuccess) return;
            }

            bool exists = sheet.Connectors.Any(c =>
                (c.Source == finalSource && c.Target == finalTarget) ||
                (c.Source == finalTarget && c.Target == finalSource));

            if (exists)
            {
                if (swarmRelation == RelationType.SwarmPublishedPort)
                {
                    _dialogService.ShowInfo(
                        "이 External Traffic과 Service 사이에는 이미 공개 포트 연결이 있습니다.\n" +
                        "같은 Service에 포트를 하나 더 공개하려면 External Traffic 노드를 하나 더 추가해 주세요.",
                        "Swarm Published Port");
                }
                else if (swarmRelation is RelationType.SwarmSecretReference or RelationType.SwarmConfigReference)
                {
                    _dialogService.ShowInfo(
                        "이 Service와 리소스는 이미 연결되어 있습니다.\n" +
                        "Target, UID, GID, Mode는 연결선을 선택해 오른쪽 속성에서 수정할 수 있습니다.",
                        "Swarm Resource Reference");
                }
                return;
            }

            {
                var newConnector = new ConnectorViewModel(finalSource, finalTarget, finalSourceDir, finalTargetDir, _dialogService);

                if (swarmRelation.HasValue)
                {
                    newConnector.RelationType = swarmRelation.Value;
                    if (swarmRelation == RelationType.VolumeMount)
                        newConnector.MountPath = "/data";
                    else if (swarmRelation == RelationType.SwarmPublishedPort)
                    {
                        var initialPort = new SwarmPublishedPortOptions(
                            80,
                            8080,
                            SwarmPortProtocol.Tcp,
                            SwarmPublishMode.Ingress);
                        if (!_dialogService.TryShowSwarmPublishedPortDialog(
                                initialPort,
                                out SwarmPublishedPortOptions configuredPort))
                        {
                            return;
                        }

                        newConnector.PublishedPort = configuredPort.PublishedPort?.ToString(
                            System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                        newConnector.TargetPort = configuredPort.TargetPort.ToString(
                            System.Globalization.CultureInfo.InvariantCulture);
                        newConnector.Protocol = configuredPort.Protocol.ToString().ToLowerInvariant();
                        newConnector.PublishMode = configuredPort.PublishMode.ToString().ToLowerInvariant();
                        newConnector.IsBidirectional = false;
                    }
                    else if (swarmRelation is RelationType.SwarmSecretReference or RelationType.SwarmConfigReference)
                    {
                        NodeViewModel? resourceNode = new[] { finalSource, finalTarget }
                            .OfType<NodeViewModel>()
                            .FirstOrDefault(node => node.ResourceKind is RuntimeResourceKind.SwarmSecret or RuntimeResourceKind.SwarmConfig);
                        if (resourceNode == null) return;

                        SwarmDataResourceKind resourceKind = swarmRelation == RelationType.SwarmSecretReference
                            ? SwarmDataResourceKind.Secret
                            : SwarmDataResourceKind.Config;
                        var initialReference = new SwarmResourceTargetOptions(resourceNode.Name);
                        if (!_dialogService.TryShowSwarmResourceReferenceDialog(
                                resourceKind,
                                resourceNode.Name,
                                initialReference,
                                out SwarmResourceTargetOptions configuredReference))
                        {
                            return;
                        }

                        newConnector.SwarmReferenceTarget = configuredReference.FileName;
                        newConnector.SwarmReferenceUid = configuredReference.Uid;
                        newConnector.SwarmReferenceGid = configuredReference.Gid;
                        newConnector.SwarmReferenceMode =
                            SwarmResourceReferenceInputParser.FormatMode(configuredReference.Mode);
                        newConnector.IsBidirectional = false;
                    }
                }
                else if (isVolumeMount)
                {
                    newConnector.RelationType = RelationType.VolumeMount;
                    newConnector.MountPath = "/data";
                }
                else if ((finalSource is NodeViewModel sourceNode && sourceNode.Type == NodeType.Internet) ||
                         (finalTarget is NodeViewModel internetTarget && internetTarget.Type == NodeType.Internet))
                {
                    newConnector.RelationType = RelationType.NetworkAttach;
                }
                else
                {
                    newConnector.RelationType = RelationType.Dependency;
                }
                sheet.Connectors.Add(newConnector);
                RecordConnectorAdd(sheet, newConnector);
            }

            IsModified = true;
        }

        private static RuntimeResourceKind GetEffectiveSwarmKind(IConnectableItem item)
        {
            if (item is ConnectableItemViewModel connectable &&
                connectable.ResourceKind != RuntimeResourceKind.Unspecified)
            {
                return connectable.ResourceKind;
            }

            return item switch
            {
                NodeViewModel { IsSwarmService: true } => RuntimeResourceKind.SwarmService,
                NodeViewModel { Type: NodeType.Volume } => RuntimeResourceKind.SwarmVolume,
                NodeViewModel { Type: NodeType.Internet } => RuntimeResourceKind.SwarmExternalTraffic,
                GroupViewModel { Type: GroupType.Network } => RuntimeResourceKind.SwarmOverlayNetwork,
                GroupViewModel => RuntimeResourceKind.SwarmVisualGroup,
                _ => RuntimeResourceKind.Unspecified
            };
        }

        private bool IsValidConnection(IConnectableItem t1, IConnectableItem t2)
        {
            bool isT1Volume = t1 is NodeViewModel n1 && n1.Type == NodeType.Volume;
            bool isT2Volume = t2 is NodeViewModel n2 && n2.Type == NodeType.Volume;
            bool isT1Internet = t1 is NodeViewModel i1 && i1.Type == NodeType.Internet;
            bool isT2Internet = t2 is NodeViewModel i2 && i2.Type == NodeType.Internet;

            if (isT1Volume && isT2Volume) return false;
            if ((isT1Internet && isT2Volume) || (isT1Volume && isT2Internet)) return false;
            return true;
        }

        // =========================================================
        // 🧱 노드(Node) 및 도커 리소스 생성 로직 모음
        // =========================================================

        public async Task CreateNodeAtAsync(object item, double x, double y, SheetViewModel? targetSheet = null)
        {
            SheetViewModel? creationSheet = targetSheet ?? ActiveSheet;
            if (creationSheet == null) return;
            IContainerService containerService = creationSheet.DockerService;
            INetworkService networkService = creationSheet.DockerService;
            var historyBefore = CaptureDiagramState(creationSheet);

            // [CASE 1] 컨테이너 (DockerContainer)
            if (item is DockerContainer container)
            {
                creationSheet.CreateNodeAt(container, x, y);
                NodeViewModel? placedNode = creationSheet.Nodes.LastOrDefault(node =>
                    string.Equals(node.ContainerId, container.Id, StringComparison.OrdinalIgnoreCase));
                IsModified = true;
                Explorer.RegisterTemplateUsage(container.Image);

                if (container.IsSwarmService && placedNode != null)
                {
                    try
                    {
                        await RestoreExistingSwarmServiceTopologyAsync(creationSheet, placedNode);
                    }
                    catch (Exception ex)
                    {
                        placedNode.IsSwarmDiagramDirty = false;
                        _dialogService.ShowError(
                            $"Service는 배치했지만 현재 ServiceSpec의 관계를 복원하지 못했습니다:\n{ex.GetBaseException().Message}",
                            "Swarm Topology Import");
                    }
                }

                if (!container.IsSwarmService && !string.IsNullOrEmpty(container.Id))
                {
                    try
                    {
                        var info = await containerService.InspectContainerAsync(container.Id);

                        // 네트워크 복구
                        if (info.NetworkSettings != null && info.NetworkSettings.Networks != null)
                        {
                            foreach (var netKvp in info.NetworkSettings.Networks)
                            {
                                string netName = netKvp.Key;
                                if (netName == "bridge") continue;

                                var existingGroup = creationSheet.Groups.FirstOrDefault(g => g.Type == GroupType.Network && g.Title == netName);

                                if (existingGroup == null)
                                {
                                    existingGroup = new GroupViewModel(x - 30, y - 40, 220, 150, networkService, _dialogService, netName, GroupType.Network)
                                    {
                                        Id = netKvp.Value.NetworkID,
                                        Driver = "bridge",
                                        External = true,
                                        IsDockerConnected = true
                                    };
                                    creationSheet.AddGroup(existingGroup);
                                }

                                var newNode = creationSheet.Nodes.LastOrDefault(n => n.ContainerId == container.Id);
                                if (newNode != null)
                                {
                                    await existingGroup.AddNodeAsync(newNode, isRestoring: true);
                                }
                            }
                        }

                        // 볼륨 복구
                        if (info.Mounts != null)
                        {
                            int volIndex = 0;
                            foreach (var mount in info.Mounts)
                            {
                                if (mount.Type == "volume")
                                {
                                    string volName = mount.Name;
                                    string destination = mount.Destination;

                                    var existingVolNode = creationSheet.Nodes.FirstOrDefault(n =>
                                        n.Type == NodeType.Volume &&
                                        (string.Equals(n.Name, volName, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(n.EffectiveVolumeName, volName, StringComparison.OrdinalIgnoreCase)));
                                    NodeViewModel targetVolNode;

                                    if (existingVolNode != null)
                                    {
                                        targetVolNode = existingVolNode;
                                    }
                                    else
                                    {
                                        var volModel = new DockerVolume { Name = volName };
                                        creationSheet.CreateNodeAt(volModel, x + 250, y + (volIndex * 120));
                                        targetVolNode = creationSheet.Nodes.Last();
                                    }

                                    var newNode = creationSheet.Nodes.LastOrDefault(n => n.ContainerId == container.Id);
                                    if (newNode != null)
                                    {
                                        bool connExists = creationSheet.Connectors.Any(c =>
                                            (c.Source == newNode && c.Target == targetVolNode) ||
                                            (c.Source == targetVolNode && c.Target == newNode));

                                        if (!connExists)
                                        {
                                            var conn = new ConnectorViewModel(newNode, targetVolNode, PortDirection.Right, PortDirection.Left, _dialogService)
                                            {
                                                RelationType = RelationType.VolumeMount,
                                                MountPath = destination
                                            };
                                            creationSheet.Connectors.Add(conn);
                                        }
                                    }
                                    volIndex++;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _dialogService.ShowError($"연관 정보 로드 실패:\n{ex.Message}", "Docker API Error");
                    }
                }
            }
            // [CASE 2] 볼륨 (DockerVolume)
            else if (item is DockerVolume volume)
            {
                creationSheet.CreateNodeAt(volume, x, y);
                IsModified = true;
            }
            // [CASE 3] 인터넷 (DockerInternet)
            else if (item is DockerInternet internet)
            {
                creationSheet.CreateNodeAt(internet, x, y);
                IsModified = true;
            }
            // [CASE 4] 네트워크 그룹 (DockerGroup)
            else if (item is DockerNetworkGroup network)
            {
                bool isSwarmOverlay = creationSheet.RuntimeKind == RuntimeKind.DockerSwarm;
                if (isSwarmOverlay && !SwarmResourceFilter.IsOverlayNetwork(network))
                {
                    _dialogService.ShowError(
                        $"'{network.Name}' is not a user-managed Swarm overlay network.",
                        "Overlay Network");
                    return;
                }

                var groupVm = new GroupViewModel(x, y, 220, 150, networkService, _dialogService, network.Name, GroupType.Network)
                {
                    Id = network.Id,
                    Driver = network.Driver,
                    ComposeNetworkName = network.Name,
                    RuntimeKind = isSwarmOverlay ? RuntimeKind.DockerSwarm : RuntimeKind.DockerEngine,
                    ResourceKind = isSwarmOverlay
                        ? RuntimeResourceKind.SwarmOverlayNetwork
                        : RuntimeResourceKind.DockerNetwork,
                    BindingState = RuntimeBindingState.Bound,
                    IsDockerConnected = true
                };
                creationSheet.AddGroup(groupVm);
                await creationSheet.RefreshGroupContainmentAsync(groupVm);
                creationSheet.UpdateGroupLayering();
                IsModified = true;
            }

            RecordAdditionsFromSnapshot(creationSheet, historyBefore, "Add diagram item", affectsDocker: false);
        }

        public async Task CreateExistingNetworkGroupAsync(
            DockerNetworkGroup network,
            double x,
            double y,
            double width,
            double height,
            SheetViewModel? targetSheet = null)
        {
            SheetViewModel? creationSheet = targetSheet ?? ActiveSheet;
            if (creationSheet == null) return;
            INetworkService networkService = creationSheet.DockerService;

            bool isSwarmOverlay = creationSheet.RuntimeKind == RuntimeKind.DockerSwarm;
            if (isSwarmOverlay && !SwarmResourceFilter.IsOverlayNetwork(network))
            {
                _dialogService.ShowError(
                    $"'{network.Name}' is not a user-managed Swarm overlay network.",
                    "Overlay Network");
                return;
            }

            var historyBefore = CaptureDiagramState(creationSheet);
            var group = new GroupViewModel(
                x,
                y,
                Math.Max(GroupViewModel.MinimumWidth, width),
                Math.Max(GroupViewModel.MinimumHeight, height),
                networkService,
                _dialogService,
                network.Name,
                GroupType.Network)
            {
                Id = network.Id,
                Driver = string.IsNullOrWhiteSpace(network.Driver) ? "bridge" : network.Driver,
                ComposeNetworkName = network.Name,
                RuntimeKind = isSwarmOverlay ? RuntimeKind.DockerSwarm : RuntimeKind.DockerEngine,
                ResourceKind = isSwarmOverlay
                    ? RuntimeResourceKind.SwarmOverlayNetwork
                    : RuntimeResourceKind.DockerNetwork,
                BindingState = RuntimeBindingState.Bound,
                External = true,
                IsDockerConnected = true
            };

            creationSheet.AddGroup(group);
            await creationSheet.RefreshGroupContainmentAsync(group);
            creationSheet.UpdateGroupLayering();
            IsModified = true;
            RecordAdditionsFromSnapshot(creationSheet, historyBefore, "Add existing Docker network", affectsDocker: false);
        }

        public Task CreateSwarmDraftNodeAsync(RuntimeResourceKind kind, double x, double y)
        {
            if (ActiveSheet?.RuntimeKind != RuntimeKind.DockerSwarm) return Task.CompletedTask;

            var historyBefore = CaptureDiagramState(ActiveSheet);
            int ordinal = ActiveSheet.Nodes.Count(node => node.ResourceKind == kind) + 1;
            var (name, type, image, isService) = kind switch
            {
                RuntimeResourceKind.SwarmService => ($"Service {ordinal}", NodeType.Container, "swarm-service", true),
                RuntimeResourceKind.SwarmVolume => ($"Volume {ordinal}", NodeType.Volume, "local", false),
                RuntimeResourceKind.SwarmExternalTraffic => ($"External Traffic {ordinal}", NodeType.Internet, "external", false),
                RuntimeResourceKind.SwarmSecret => ($"Secret {ordinal}", NodeType.Container, "swarm-secret", false),
                RuntimeResourceKind.SwarmConfig => ($"Config {ordinal}", NodeType.Container, "swarm-config", false),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "지원하지 않는 Swarm 노드 종류입니다.")
            };

            var node = new NodeViewModel(_containerService, _volumeService, _dialogService)
            {
                Name = name,
                ImageName = image,
                Type = type,
                X = x,
                Y = y,
                RuntimeKind = RuntimeKind.DockerSwarm,
                ResourceKind = kind,
                BindingState = RuntimeBindingState.Draft,
                IsSwarmService = isService,
                SwarmMode = isService ? "replicated" : string.Empty,
                SwarmDesiredReplicas = isService ? 1UL : 0UL,
                TargetSwarmReplicas = isService ? 1UL : 0UL,
                DetailStatus = "Draft",
                StatusColor = "#7652A8",
                IsDockerConnected = false
            };

            ActiveSheet.Nodes.Add(node);
            IsModified = true;
            RecordAdditionsFromSnapshot(ActiveSheet, historyBefore, $"Add draft {name}", affectsDocker: false);
            return Task.CompletedTask;
        }

        public Task CreateSwarmVolumeDraftNodeAsync(VolumeCreateOptions options, double x, double y)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (ActiveSheet?.RuntimeKind != RuntimeKind.DockerSwarm) return Task.CompletedTask;

            string displayName = options.Name.Trim();
            if (displayName.Length == 0)
                throw new ArgumentException("Volume 이름이 비어 있습니다.", nameof(options));

            string sourceName = options.EffectiveDockerVolumeName.Trim();
            string driver = string.IsNullOrWhiteSpace(options.Driver) ? "local" : options.Driver.Trim();
            var historyBefore = CaptureDiagramState(ActiveSheet);
            var node = new NodeViewModel(_containerService, _volumeService, _dialogService)
            {
                Name = displayName,
                DockerVolumeName = sourceName,
                Driver = driver,
                ImageName = driver,
                Type = NodeType.Volume,
                X = x,
                Y = y,
                RuntimeKind = RuntimeKind.DockerSwarm,
                ResourceKind = RuntimeResourceKind.SwarmVolume,
                BindingState = RuntimeBindingState.Draft,
                VolumeExternal = options.External,
                VolumeLabels = new Dictionary<string, string>(options.Labels),
                VolumeDriverOptions = new Dictionary<string, string>(options.DriverOptions),
                DetailStatus = options.External
                    ? $"Draft · existing {sourceName}"
                    : $"Draft · {driver}",
                StatusColor = "#7652A8",
                IsDockerConnected = false
            };

            ActiveSheet.Nodes.Add(node);
            IsModified = true;
            RecordAdditionsFromSnapshot(ActiveSheet, historyBefore, $"Add draft volume {displayName}", affectsDocker: false);
            return Task.CompletedTask;
        }

        public async Task CreateSwarmDraftGroupAsync(
            RuntimeResourceKind kind,
            double x,
            double y,
            double width,
            double height)
        {
            SheetViewModel? sheet = ActiveSheet;
            if (sheet?.RuntimeKind != RuntimeKind.DockerSwarm) return;
            if (kind != RuntimeResourceKind.SwarmVisualGroup)
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "지원하지 않는 Swarm 그룹 종류입니다.");

            var historyBefore = CaptureDiagramState(sheet);
            int ordinal = sheet.Groups.Count(group => group.ResourceKind == kind) + 1;
            var group = new GroupViewModel(
                x,
                y,
                Math.Max(GroupViewModel.MinimumWidth, width),
                Math.Max(GroupViewModel.MinimumHeight, height),
                sheet.DockerService,
                _dialogService,
                $"Group {ordinal}",
                GroupType.General)
            {
                RuntimeKind = RuntimeKind.DockerSwarm,
                ResourceKind = kind,
                BindingState = RuntimeBindingState.Draft,
                Driver = string.Empty,
                IsDockerConnected = false
            };

            sheet.AddGroup(group);
            await sheet.RefreshGroupContainmentAsync(group);
            sheet.UpdateGroupLayering();
            IsModified = true;
            RecordAdditionsFromSnapshot(sheet, historyBefore, $"Add draft {group.Title}", affectsDocker: false);
        }

        public async Task CreateNewNetworkGroupAsync(string name, string driver, double x, double y, double w, double h)
        {
            await CreateNewNetworkGroupAsync(NetworkCreateOptions.Basic(name, driver), x, y, w, h);
        }

        public async Task CreateNewNetworkGroupAsync(NetworkCreateOptions options, double x, double y, double w, double h, SheetViewModel? targetSheet = null)
        {
            SheetViewModel? creationSheet = targetSheet ?? ActiveSheet;
            if (string.IsNullOrWhiteSpace(options.Name) || creationSheet == null) return;
            INetworkService networkService = creationSheet.DockerService;

            string requestedNetworkName = options.Name.Trim();
            string externalDockerName = string.IsNullOrWhiteSpace(options.ComposeNetworkName)
                ? requestedNetworkName
                : options.ComposeNetworkName.Trim();
            string? resolvedName = await _resourceNames.ResolveNetworkNameAsync(creationSheet, networkService, requestedNetworkName, options.External);
            if (resolvedName == null) return;

            options.Name = resolvedName;
            if (options.External &&
                !string.Equals(resolvedName, requestedNetworkName, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(options.ComposeNetworkName))
            {
                options.ComposeNetworkName = externalDockerName;
            }

            var historyBefore = CaptureDiagramState(creationSheet);

            try
            {
                string networkId;
                if (options.External)
                {
                    var dockerNetworkName = string.IsNullOrWhiteSpace(options.ComposeNetworkName) ? options.Name : options.ComposeNetworkName;
                    var networks = await networkService.GetNetworksAsync();
                    var existingNetwork = networks.FirstOrDefault(n => string.Equals(n.Name, dockerNetworkName, StringComparison.OrdinalIgnoreCase));
                    if (existingNetwork == null)
                    {
                        _dialogService.ShowError($"외부 네트워크 '{dockerNetworkName}'을(를) Docker에서 찾을 수 없습니다.\n먼저 Docker에 해당 네트워크를 만든 뒤 다시 시도하세요.", "External Network");
                        return;
                    }

                    networkId = existingNetwork.Id;
                    options.Driver = existingNetwork.Driver;
                }
                else
                {
                    networkId = await networkService.CreateNetworkAsync(options);
                }

                var newNetworkGroup = new GroupViewModel(x, y, w, h, networkService, _dialogService, options.Name, GroupType.Network)
                {
                    Id = networkId,
                    Driver = options.Driver,
                    Subnet = options.Subnet,
                    Gateway = options.Gateway,
                    IpRange = options.IpRange,
                    Internal = options.Internal,
                    Attachable = options.Attachable,
                    EnableIPv6 = options.EnableIPv6,
                    External = options.External,
                    ComposeNetworkName = options.ComposeNetworkName,
                    ComposeRawNetworkYaml = options.ComposeRawNetworkYaml,
                    Labels = new Dictionary<string, string>(options.Labels),
                    DriverOptions = new Dictionary<string, string>(options.DriverOptions),
                    AuxAddresses = new Dictionary<string, string>(options.AuxAddresses),
                    IsDockerConnected = true,
                    ParentSheet = creationSheet
                };

                creationSheet.Groups.Add(newNetworkGroup);
                creationSheet.UpdateGroupLayering();

                await creationSheet.RefreshGroupContainmentAsync(newNetworkGroup);

                IsModified = true;
                RecordAdditionsFromSnapshot(creationSheet, historyBefore, $"Create network {options.Name}", !options.External && History.IncludeDockerResourceHistory);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"'{options.Name}' 네트워크 생성에 실패했습니다:\n{ex.Message}", "Network Create Error");
            }
        }

        public async Task CreateSwarmOverlayNetworkGroupAsync(
            NetworkCreateOptions options,
            double x,
            double y,
            double width,
            double height,
            SheetViewModel? targetSheet = null)
        {
            SheetViewModel? creationSheet = targetSheet ?? ActiveSheet;
            if (creationSheet?.RuntimeKind != RuntimeKind.DockerSwarm ||
                string.IsNullOrWhiteSpace(options.Name))
            {
                return;
            }
            INetworkService networkService = creationSheet.DockerService;

            string requestedNetworkName = options.Name.Trim();
            if (requestedNetworkName.Equals("ingress", StringComparison.OrdinalIgnoreCase))
            {
                _dialogService.ShowError(
                    "'ingress' is the Swarm routing-mesh network and cannot be created or managed here.",
                    "Overlay Network");
                return;
            }

            string externalDockerName = string.IsNullOrWhiteSpace(options.ComposeNetworkName)
                ? requestedNetworkName
                : options.ComposeNetworkName.Trim();
            string? resolvedName = await _resourceNames.ResolveNetworkNameAsync(
                creationSheet,
                networkService,
                requestedNetworkName,
                options.External);
            if (resolvedName == null) return;

            options.Name = resolvedName;
            if (options.External &&
                !string.Equals(resolvedName, requestedNetworkName, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(options.ComposeNetworkName))
            {
                options.ComposeNetworkName = externalDockerName;
            }

            var historyBefore = CaptureDiagramState(creationSheet);
            try
            {
                string networkId;
                if (options.External)
                {
                    string dockerNetworkName = string.IsNullOrWhiteSpace(options.ComposeNetworkName)
                        ? options.Name
                        : options.ComposeNetworkName.Trim();
                    List<DockerNetworkGroup> networks = await networkService.GetNetworksAsync();
                    DockerNetworkGroup? existingNetwork = networks.FirstOrDefault(network =>
                        string.Equals(network.Name, dockerNetworkName, StringComparison.OrdinalIgnoreCase));
                    if (existingNetwork == null)
                    {
                        _dialogService.ShowError(
                            $"Existing overlay network '{dockerNetworkName}' was not found on the Swarm manager.",
                            "External Overlay Network");
                        return;
                    }

                    if (!SwarmResourceFilter.IsOverlayNetwork(existingNetwork))
                    {
                        _dialogService.ShowError(
                            $"'{dockerNetworkName}' is not a user-managed overlay network. " +
                            "Select an overlay network other than the built-in ingress network.",
                            "External Overlay Network");
                        return;
                    }

                    networkId = existingNetwork.Id;
                    options.Driver = existingNetwork.Driver;
                    options.ComposeNetworkName = existingNetwork.Name;
                }
                else
                {
                    options.Driver = "overlay";
                    networkId = await networkService.CreateNetworkAsync(options);
                }

                var group = new GroupViewModel(
                    x,
                    y,
                    Math.Max(GroupViewModel.MinimumWidth, width),
                    Math.Max(GroupViewModel.MinimumHeight, height),
                    networkService,
                    _dialogService,
                    options.Name,
                    GroupType.Network)
                {
                    Id = networkId,
                    Driver = "overlay",
                    Subnet = options.Subnet,
                    Gateway = options.Gateway,
                    IpRange = options.IpRange,
                    Internal = options.Internal,
                    Attachable = options.Attachable,
                    EnableIPv6 = options.EnableIPv6,
                    External = options.External,
                    ComposeNetworkName = options.ComposeNetworkName,
                    ComposeRawNetworkYaml = options.ComposeRawNetworkYaml,
                    Labels = new Dictionary<string, string>(options.Labels),
                    DriverOptions = new Dictionary<string, string>(options.DriverOptions),
                    AuxAddresses = new Dictionary<string, string>(options.AuxAddresses),
                    RuntimeKind = RuntimeKind.DockerSwarm,
                    ResourceKind = RuntimeResourceKind.SwarmOverlayNetwork,
                    BindingState = RuntimeBindingState.Bound,
                    IsDockerConnected = true,
                    ParentSheet = creationSheet
                };

                creationSheet.AddGroup(group);
                await creationSheet.RefreshGroupContainmentAsync(group);
                creationSheet.UpdateGroupLayering();
                IsModified = true;
                RecordAdditionsFromSnapshot(
                    creationSheet,
                    historyBefore,
                    options.External
                        ? $"Add overlay network {options.Name}"
                        : $"Create overlay network {options.Name}",
                    affectsDocker: false);
                if (ReferenceEquals(ActiveSheet, creationSheet))
                    await RefreshRuntimeResourcesAsync();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(
                    $"Failed to create overlay network '{options.Name}':\n{ex.GetBaseException().Message}",
                    "Overlay Network Create Error");
            }
        }

        public async Task CreateNewVolumeNodeAsync(string name, string driver, double x, double y)
        {
            await CreateNewVolumeNodeAsync(VolumeCreateOptions.Basic(name, driver), x, y);
        }

        public async Task CreateNewVolumeNodeAsync(VolumeCreateOptions options, double x, double y, SheetViewModel? targetSheet = null)
        {
            SheetViewModel? creationSheet = targetSheet ?? ActiveSheet;
            if (creationSheet == null) return;
            IContainerService containerService = creationSheet.DockerService;
            IVolumeService volumeService = creationSheet.DockerService;
            var historyBefore = CaptureDiagramState(creationSheet);
            string displayName = options.Name.Trim();
            string dockerVolumeName = options.EffectiveDockerVolumeName.Trim();
            string driver = string.IsNullOrWhiteSpace(options.Driver) ? "local" : options.Driver.Trim();
            options.Name = displayName;
            options.DockerVolumeName = dockerVolumeName;
            options.Driver = driver;

            var resolvedNames = await _resourceNames.ResolveVolumeNamesAsync(creationSheet, volumeService, displayName, dockerVolumeName, options.External);
            if (resolvedNames == null) return;
            displayName = resolvedNames.Value.DisplayName;
            dockerVolumeName = resolvedNames.Value.DockerName;
            options.Name = displayName;
            options.DockerVolumeName = dockerVolumeName;

            var node = new NodeViewModel(containerService, volumeService, _dialogService)
            {
                Name = $"{displayName} (Creating...)",
                ImageName = driver,
                Type = NodeType.Volume,
                X = x,
                Y = y,
                IsCreating = true,
                StatusColor = "#FFC107"
            };
            creationSheet.Nodes.Add(node);
            Func<Task> retryVolumeCreation = async () =>
            {
                await CreateNewVolumeNodeAsync(options, x, y, creationSheet);
            };

            try
            {
                if (options.External)
                {
                    var existing = await volumeService.InspectVolumeAsync(dockerVolumeName);
                    driver = string.IsNullOrWhiteSpace(existing.Driver) ? driver : existing.Driver;
                }
                else
                {
                    await volumeService.CreateVolumeAsync(options);
                }

                node.Name = displayName;
                node.DockerVolumeName = dockerVolumeName;
                node.VolumeExternal = options.External;
                node.VolumeLabels = new Dictionary<string, string>(options.Labels);
                node.VolumeDriverOptions = new Dictionary<string, string>(options.DriverOptions);
                node.ContainerId = "";
                node.Driver = driver;
                node.ImageName = driver;
                node.IsDockerConnected = true;

                node.ClearCreationFailure();
                node.IsCreating = false;
                node.StatusColor = "#E67E22";
                node.IsDockerConnected = true;
                RecordAdditionsFromSnapshot(
                    creationSheet,
                    historyBefore,
                    options.External ? $"Add external volume {dockerVolumeName}" : $"Create volume {dockerVolumeName}",
                    !options.External && History.IncludeDockerResourceHistory);
            }
            catch (Exception ex)
            {
                node.MarkCreationFailed($"볼륨 생성 실패:\n{ex.Message}", retryVolumeCreation);
                _dialogService.ShowMessage($"볼륨 생성 실패: {ex.Message}");
            }
        }

        public async Task<bool> ConnectVolumeToContainerAsync(NodeViewModel containerNode, NodeViewModel volumeNode)
        {
            if (!_dialogService.TryShowMountDialog(out string mountPath, out string owner)) return false;

            IContainerService containerService = containerNode.ParentSheet?.DockerService
                                                 ?? ActiveSheet?.DockerService
                                                 ?? _defaultDockerService;
            string containerId = containerNode.ContainerId;
            string volumeName = volumeNode.EffectiveVolumeName;

            bool keepBackup = false;
            bool originalRemoved = false;
            bool wasRunning = false;
            string? replacementId = null;
            ContainerInspectResponse? inspect = null;
            string tempHostPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "docker_backup_" + Guid.NewGuid());

            _dialogService.SetBusyCursor(true);

            try
            {
                inspect = await containerService.InspectContainerAsync(containerId);
                wasRunning = inspect.State?.Running == true;
                if (wasRunning && inspect.HostConfig?.AutoRemove != true)
                {
                    await containerService.StopContainerAsync(containerId);
                }

                if (!System.IO.Directory.Exists(tempHostPath))
                    System.IO.Directory.CreateDirectory(tempHostPath);

                try
                {
                    await containerService.CopyFromContainerAsync(containerId, mountPath, tempHostPath);
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("No such") || ex.Message.Contains("NotFound") || ex.Message.Contains("404"))
                    {
                        Debug.WriteLine($"[Backup Skip] '{mountPath}' 경로가 컨테이너에 아직 존재하지 않아 백업을 생략합니다.");
                    }
                    else
                    {
                        bool proceed = _dialogService.ShowConfirm(
                            $"기존 데이터 백업 중 예상치 못한 오류가 발생했습니다.\n" +
                            $"이대로 진행하면 컨테이너 내부의 기존 데이터가 유실될 위험이 있습니다.\n\n" +
                            $"[오류 내용]\n{ex.Message}\n\n" +
                            $"위험을 감수하고 데이터 없이 마운트를 강행하시겠습니까?",
                            "⚠️ 데이터 백업 실패 경고"
                        );

                        if (!proceed)
                        {
                            if (wasRunning && inspect.HostConfig?.AutoRemove != true)
                                await containerService.StartContainerAsync(containerId);
                            return false;
                        }
                    }
                }

                var volumes = inspect.HostConfig?.Binds?.ToList() ?? new List<string>();
                volumes.RemoveAll(bind => bind.StartsWith(volumeName + ":", StringComparison.OrdinalIgnoreCase));
                volumes.Add($"{volumeName}:{mountPath}");

                await containerService.RemoveContainerAsync(containerId);
                originalRemoved = true;

                replacementId = await containerService.RecreateContainerFromInspectAsync(
                    inspect.Name,
                    inspect,
                    volumes,
                    startContainer: true);

                string folderName = System.IO.Path.GetFileName(mountPath.TrimEnd('/'));
                string actualSourcePath = System.IO.Path.Combine(tempHostPath, folderName);

                if (System.IO.Directory.Exists(actualSourcePath))
                {
                    await containerService.CopyToContainerAsync(replacementId, actualSourcePath, mountPath);
                }
                else
                {
                    await containerService.CopyToContainerAsync(replacementId, tempHostPath, mountPath);
                }

                if (!string.IsNullOrWhiteSpace(owner))
                {
                    string quotedPath = "'" + mountPath.Replace("'", "'\"'\"'") + "'";
                    ExecCommandResult result = await containerService.ExecuteCommandWithOutputAsync(
                        replacementId,
                        $"chown -R {owner} {quotedPath}");
                    if (result.ExitCode != 0)
                        throw new InvalidOperationException($"볼륨 소유권 변경 실패: {result.Stderr}");
                }

                if (!wasRunning)
                    await containerService.StopContainerAsync(replacementId);

                containerNode.ContainerId = replacementId;
                await containerNode.RefreshDetailsAsync();

                _dialogService.ShowMessage("볼륨 연결 완료!");
                return true;
            }
            catch (Exception ex)
            {
                keepBackup = true;
                string recoveryMessage = string.Empty;
                if (originalRemoved && inspect != null)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(replacementId))
                        {
                            try { await containerService.RemoveContainerAsync(replacementId); } catch { }
                        }

                        string restoredId = await containerService.RecreateContainerFromInspectAsync(
                            inspect.Name,
                            inspect,
                            inspect.HostConfig?.Binds?.ToList() ?? new List<string>(),
                            wasRunning);
                        containerNode.ContainerId = restoredId;
                        await containerNode.RefreshDetailsAsync();
                        keepBackup = false;
                        recoveryMessage = "\n원본 컨테이너 설정은 복구했습니다.";
                    }
                    catch (Exception recoveryEx)
                    {
                        recoveryMessage = $"\n원본 컨테이너 자동 복구도 실패했습니다: {recoveryEx.GetBaseException().Message}";
                    }
                }
                else if (wasRunning)
                {
                    try { await containerService.StartContainerAsync(containerId); } catch { }
                }

                string backupMessage = keepBackup ? $"\n\n백업: {tempHostPath}" : string.Empty;
                _dialogService.ShowMessage($"오류 발생: {ex.GetBaseException().Message}{recoveryMessage}{backupMessage}");
                return false;
            }
            finally
            {
                _dialogService.SetBusyCursor(false);
                if (!keepBackup && Directory.Exists(tempHostPath)) Directory.Delete(tempHostPath, true);
            }
        }
    }
}
