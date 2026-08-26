using DockerDiagram.Diagram;
using Docker.DotNet.Models;
using DockerDiagram.Models;
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

            if (!IsValidConnection(source, target))
            {
                _dialogService.ShowMessage("연결할 수 없는 조합입니다.\n(볼륨끼리 연결하거나, 인터넷과 볼륨은 연결할 수 없습니다.)");
                return;
            }

            IConnectableItem finalSource = source;
            IConnectableItem finalTarget = target;
            PortDirection finalSourceDir = sourceDir;
            PortDirection finalTargetDir = targetDir;

            NodeViewModel? volumeContainer = new[] { finalSource, finalTarget }
                .OfType<NodeViewModel>()
                .FirstOrDefault(node => node.Type == NodeType.Container);
            NodeViewModel? volumeNode = new[] { finalSource, finalTarget }
                .OfType<NodeViewModel>()
                .FirstOrDefault(node => node.Type == NodeType.Volume);
            bool isVolumeMount = volumeContainer != null && volumeNode != null;

            // 볼륨 마운트는 연결 방향과 무관하게 양 끝의 리소스 역할로 처리합니다.
            if (isVolumeMount)
            {
                bool isSuccess = await ConnectVolumeToContainerAsync(volumeContainer!, volumeNode!);
                if (!isSuccess) return;
            }

            bool exists = ActiveSheet.Connectors.Any(c =>
                (c.Source == finalSource && c.Target == finalTarget) ||
                (c.Source == finalTarget && c.Target == finalSource));

            if (!exists)
            {
                var newConnector = new ConnectorViewModel(finalSource, finalTarget, finalSourceDir, finalTargetDir, _dialogService);

                if (isVolumeMount)
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
                ActiveSheet.Connectors.Add(newConnector);
                RecordConnectorAdd(ActiveSheet, newConnector);
            }

            IsModified = true;
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

        public async Task CreateNodeAtAsync(object item, double x, double y)
        {
            if (ActiveSheet == null) return;
            var historyBefore = CaptureDiagramState(ActiveSheet);

            // [CASE 1] 컨테이너 (DockerContainer)
            if (item is DockerContainer container)
            {
                ActiveSheet.CreateNodeAt(container, x, y);
                IsModified = true;
                Explorer.RegisterTemplateUsage(container.Image);

                if (!container.IsSwarmService && !string.IsNullOrEmpty(container.Id))
                {
                    try
                    {
                        var info = await _containerService.InspectContainerAsync(container.Id);

                        // 네트워크 복구
                        if (info.NetworkSettings != null && info.NetworkSettings.Networks != null)
                        {
                            foreach (var netKvp in info.NetworkSettings.Networks)
                            {
                                string netName = netKvp.Key;
                                if (netName == "bridge") continue;

                                var existingGroup = ActiveSheet.Groups.FirstOrDefault(g => g.Type == GroupType.Network && g.Title == netName);

                                if (existingGroup == null)
                                {
                                    existingGroup = new GroupViewModel(x - 30, y - 40, 220, 150, _networkService, _dialogService, netName, GroupType.Network)
                                    {
                                        IsDockerConnected = true
                                    };
                                    ActiveSheet.AddGroup(existingGroup);
                                }

                                var newNode = ActiveSheet.Nodes.LastOrDefault(n => n.ContainerId == container.Id);
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

                                    var existingVolNode = ActiveSheet.Nodes.FirstOrDefault(n =>
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
                                        ActiveSheet.CreateNodeAt(volModel, x + 250, y + (volIndex * 120));
                                        targetVolNode = ActiveSheet.Nodes.Last();
                                    }

                                    var newNode = ActiveSheet.Nodes.LastOrDefault(n => n.ContainerId == container.Id);
                                    if (newNode != null)
                                    {
                                        bool connExists = ActiveSheet.Connectors.Any(c =>
                                            (c.Source == newNode && c.Target == targetVolNode) ||
                                            (c.Source == targetVolNode && c.Target == newNode));

                                        if (!connExists)
                                        {
                                            var conn = new ConnectorViewModel(newNode, targetVolNode, PortDirection.Right, PortDirection.Left, _dialogService)
                                            {
                                                RelationType = RelationType.VolumeMount,
                                                MountPath = destination
                                            };
                                            ActiveSheet.Connectors.Add(conn);
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
                ActiveSheet.CreateNodeAt(volume, x, y);
                IsModified = true;
            }
            // [CASE 3] 인터넷 (DockerInternet)
            else if (item is DockerInternet internet)
            {
                ActiveSheet.CreateNodeAt(internet, x, y);
                IsModified = true;
            }
            // [CASE 4] 네트워크 그룹 (DockerGroup)
            else if (item is DockerNetworkGroup network)
            {
                var groupVm = new GroupViewModel(x, y, 220, 150, _networkService, _dialogService, network.Name, GroupType.Network)
                {
                    Id = network.Id,
                    Driver = network.Driver,
                    IsDockerConnected = true
                };
                ActiveSheet.AddGroup(groupVm);
                IsModified = true;
            }

            RecordAdditionsFromSnapshot(ActiveSheet, historyBefore, "Add diagram item", affectsDocker: false);
        }

        public Task CreateExistingNetworkGroupAsync(
            DockerNetworkGroup network,
            double x,
            double y,
            double width,
            double height)
        {
            if (ActiveSheet == null) return Task.CompletedTask;

            var historyBefore = CaptureDiagramState(ActiveSheet);
            var group = new GroupViewModel(
                x,
                y,
                Math.Max(GroupViewModel.MinimumWidth, width),
                Math.Max(GroupViewModel.MinimumHeight, height),
                _networkService,
                _dialogService,
                network.Name,
                GroupType.Network)
            {
                Id = network.Id,
                Driver = string.IsNullOrWhiteSpace(network.Driver) ? "bridge" : network.Driver,
                ComposeNetworkName = network.Name,
                IsDockerConnected = true
            };

            ActiveSheet.AddGroup(group);
            ActiveSheet.UpdateGroupLayering();
            IsModified = true;
            RecordAdditionsFromSnapshot(ActiveSheet, historyBefore, "Add existing Docker network", affectsDocker: false);
            return Task.CompletedTask;
        }
        public async Task CreateNewNetworkGroupAsync(string name, string driver, double x, double y, double w, double h)
        {
            await CreateNewNetworkGroupAsync(NetworkCreateOptions.Basic(name, driver), x, y, w, h);
        }

        public async Task CreateNewNetworkGroupAsync(NetworkCreateOptions options, double x, double y, double w, double h)
        {
            if (string.IsNullOrWhiteSpace(options.Name) || ActiveSheet == null) return;

            string requestedNetworkName = options.Name.Trim();
            string externalDockerName = string.IsNullOrWhiteSpace(options.ComposeNetworkName)
                ? requestedNetworkName
                : options.ComposeNetworkName.Trim();
            string? resolvedName = await _resourceNames.ResolveNetworkNameAsync(ActiveSheet, _networkService, requestedNetworkName, options.External);
            if (resolvedName == null) return;

            options.Name = resolvedName;
            if (options.External &&
                !string.Equals(resolvedName, requestedNetworkName, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(options.ComposeNetworkName))
            {
                options.ComposeNetworkName = externalDockerName;
            }

            var historyBefore = CaptureDiagramState(ActiveSheet);

            try
            {
                string networkId;
                if (options.External)
                {
                    var dockerNetworkName = string.IsNullOrWhiteSpace(options.ComposeNetworkName) ? options.Name : options.ComposeNetworkName;
                    var networks = await _networkService.GetNetworksAsync();
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
                    networkId = await _networkService.CreateNetworkAsync(options);
                }

                var newNetworkGroup = new GroupViewModel(x, y, w, h, _networkService, _dialogService, options.Name, GroupType.Network)
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
                    ParentSheet = this.ActiveSheet
                };

                ActiveSheet.Groups.Add(newNetworkGroup);
                ActiveSheet.UpdateGroupLayering();

                await ActiveSheet.RefreshGroupContainmentAsync(newNetworkGroup);

                IsModified = true;
                RecordAdditionsFromSnapshot(ActiveSheet, historyBefore, $"Create network {options.Name}", !options.External && History.IncludeDockerResourceHistory);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"'{options.Name}' 네트워크 생성에 실패했습니다:\n{ex.Message}", "Network Create Error");
            }
        }

        public async Task CreateNewVolumeNodeAsync(string name, string driver, double x, double y)
        {
            await CreateNewVolumeNodeAsync(VolumeCreateOptions.Basic(name, driver), x, y);
        }

        public async Task CreateNewVolumeNodeAsync(VolumeCreateOptions options, double x, double y)
        {
            if (ActiveSheet == null) return;
            var historyBefore = CaptureDiagramState(ActiveSheet);
            string displayName = options.Name.Trim();
            string dockerVolumeName = options.EffectiveDockerVolumeName.Trim();
            string driver = string.IsNullOrWhiteSpace(options.Driver) ? "local" : options.Driver.Trim();
            options.Name = displayName;
            options.DockerVolumeName = dockerVolumeName;
            options.Driver = driver;

            var resolvedNames = await _resourceNames.ResolveVolumeNamesAsync(ActiveSheet, _volumeService, displayName, dockerVolumeName, options.External);
            if (resolvedNames == null) return;
            displayName = resolvedNames.Value.DisplayName;
            dockerVolumeName = resolvedNames.Value.DockerName;
            options.Name = displayName;
            options.DockerVolumeName = dockerVolumeName;

            var node = new NodeViewModel(_containerService, _volumeService, _dialogService)
            {
                Name = $"{displayName} (Creating...)",
                ImageName = driver,
                Type = NodeType.Volume,
                X = x,
                Y = y,
                IsCreating = true,
                StatusColor = "#FFC107"
            };
            ActiveSheet.Nodes.Add(node);
            var creationSheet = ActiveSheet;
            Func<Task> retryVolumeCreation = async () =>
            {
                ActiveSheet = creationSheet;
                await CreateNewVolumeNodeAsync(options, x, y);
            };

            try
            {
                if (options.External)
                {
                    var existing = await _volumeService.InspectVolumeAsync(dockerVolumeName);
                    driver = string.IsNullOrWhiteSpace(existing.Driver) ? driver : existing.Driver;
                }
                else
                {
                    await _volumeService.CreateVolumeAsync(options);
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
                    ActiveSheet,
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

            string containerId = containerNode.ContainerId;
            string volumeName = volumeNode.Name;

            bool keepBackup = false;
            string tempHostPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "docker_backup_" + Guid.NewGuid());

            _dialogService.SetBusyCursor(true);

            try
            {
                if (containerNode.IsRunning)
                {
                    await _containerService.StopContainerAsync(containerId);
                }

                if (!System.IO.Directory.Exists(tempHostPath))
                    System.IO.Directory.CreateDirectory(tempHostPath);

                try
                {
                    await _containerService.CopyFromContainerAsync(containerId, mountPath, tempHostPath);
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
                            if (System.IO.Directory.Exists(tempHostPath)) System.IO.Directory.Delete(tempHostPath, true);
                            return false;
                        }
                    }
                }

                var inspect = await _containerService.InspectContainerAsync(containerId);
                var oldConfig = inspect.Config;
                var oldHostConfig = inspect.HostConfig;

                string imageName = oldConfig.Image;
                string imgRepo = imageName;
                string imgTag = "latest";
                int lastColonIndex = imageName.LastIndexOf(':');
                if (lastColonIndex > 0)
                {
                    imgRepo = imageName[..lastColonIndex];
                    imgTag = imageName[(lastColonIndex + 1)..];
                }

                var ports = new List<string>();
                if (oldHostConfig.PortBindings != null)
                {
                    foreach (var pb in oldHostConfig.PortBindings)
                    {
                        string containerPort = pb.Key.Split('/')[0];
                        if (pb.Value != null && pb.Value.Count > 0)
                            ports.Add($"{pb.Value[0].HostPort}:{containerPort}");
                    }
                }

                var envs = oldConfig.Env != null ? oldConfig.Env.ToList() : new List<string>();

                var volumes = new List<string>();
                if (oldHostConfig.Binds != null) volumes.AddRange(oldHostConfig.Binds);
                volumes.Add($"{volumeName}:{mountPath}");

                string command = oldConfig.Cmd != null ? string.Join(" ", oldConfig.Cmd) : "";
                bool tty = oldConfig.Tty;

                await _containerService.RemoveContainerAsync(containerId);

                string newId = await _containerService.CreateAndStartContainerAsync(
                    containerNode.Name, imgRepo, imgTag, ports, envs, volumes,
                    oldHostConfig.RestartPolicy.Name.ToString(), 0, 0,
                    command,
                    tty
                );

                string folderName = System.IO.Path.GetFileName(mountPath.TrimEnd('/'));
                string actualSourcePath = System.IO.Path.Combine(tempHostPath, folderName);

                if (System.IO.Directory.Exists(actualSourcePath))
                {
                    await _containerService.CopyToContainerAsync(newId, actualSourcePath, mountPath);
                }
                else
                {
                    await _containerService.CopyToContainerAsync(newId, tempHostPath, mountPath);
                }

                if (!string.IsNullOrWhiteSpace(owner))
                {
                    string cmd = $"chown -R {owner} {mountPath}";
                    await _containerService.ExecuteCommandAsync(newId, cmd);
                }

                containerNode.ContainerId = newId;
                await containerNode.RefreshDetailsAsync();

                _dialogService.ShowMessage("볼륨 연결 완료!");
                return true;
            }
            catch (Exception ex)
            {
                keepBackup = true;
                _dialogService.ShowMessage($"오류 발생: {ex.Message}");
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
