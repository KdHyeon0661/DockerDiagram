using DockerDiagram.Diagram;
using DockerDiagram.Infrastructure;
using DockerDiagram.Contracts;
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
        public async Task CreateNewContainerNodeAsync(string name, string image, string tag, List<string> ports, List<string> envs, List<string> volumes, string restartPolicy, long memoryMb, double cpuCount, double x, double y, string networkName = "bridge", string command = "", bool tty = false, string? regUser = null, string? regPass = null, string? regServer = null, SheetViewModel? targetSheet = null)
        {
            SheetViewModel? creationSheet = targetSheet ?? ActiveSheet;
            if (creationSheet == null) return;
            IDockerService dockerService = creationSheet.DockerService;
            IContainerService containerService = dockerService;
            IVolumeService volumeService = dockerService;
            INetworkService networkService = dockerService;
            IImageService imageService = dockerService;
            var historyBefore = CaptureDiagramState(creationSheet);
            var safePorts = ports ?? new List<string>();
            var safeEnvs = envs ?? new List<string>();
            var safeVolumes = volumes ?? new List<string>();

            string? resolvedName = await _resourceNames.ResolveContainerNameAsync(creationSheet, containerService, name);
            if (resolvedName == null) return;
            name = resolvedName;

            if (safePorts.Count > 0)
            {
                var newHostPorts = safePorts.Select(p => p.Split(':')[0]).ToList();
                var existingContainers = creationSheet.Nodes.Where(n => n.Type == NodeType.Container && n.PortBindings != null);

                foreach (var existingNode in existingContainers)
                {
                    var existingHostPorts = existingNode.PortBindings.Select(p => p.Split(':')[0]);
                    var conflictedPort = newHostPorts.FirstOrDefault(p => existingHostPorts.Contains(p));
                    if (conflictedPort != null)
                    {
                        _dialogService.ShowError($"호스트 포트 '{conflictedPort}'는 이미 '{existingNode.Name}' 컨테이너가 사용 중입니다.\n충돌을 방지하기 위해 작업을 취소합니다.", "포트 충돌 경고");
                        return;
                    }
                }
            }

            var namedVolumesToDraw = new List<string>();
            foreach (var vol in safeVolumes)
            {
                bool isBindMount = System.Text.RegularExpressions.Regex.IsMatch(vol, @"^([a-zA-Z]:[\\/]|/|\.|~)");
                if (!isBindMount) namedVolumesToDraw.Add(vol);
            }

            ExistingVolumeSnapshot existingVolumes = await GetExistingVolumeNamesAsync(volumeService);

            (image, tag) = DockerImageReferenceParser.Split(image, tag);

            GroupViewModel? targetGroup = null;

            if (!string.IsNullOrWhiteSpace(networkName) && networkName != "bridge" && networkName != "host" && networkName != "none")
            {
                targetGroup = creationSheet.Groups.FirstOrDefault(g => g.Type == GroupType.Network && g.Title == networkName);

                if (targetGroup == null)
                {
                    DockerNetworkGroup? existingNetwork = null;
                    try
                    {
                        existingNetwork = (await networkService.GetNetworksAsync())
                            .FirstOrDefault(network => string.Equals(network.Name, networkName, StringComparison.OrdinalIgnoreCase));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[DockerDiscovery] 기존 네트워크 소유권 조회 실패: {ex.Message}");
                    }
                    targetGroup = new GroupViewModel(x, y, 220, 150, networkService, _dialogService, networkName, GroupType.Network)
                    {
                        Id = existingNetwork?.Id ?? string.Empty,
                        Driver = string.IsNullOrWhiteSpace(existingNetwork?.Driver) ? "bridge" : existingNetwork.Driver,
                        External = existingNetwork != null,
                        IsDockerConnected = existingNetwork != null
                    };
                    creationSheet.AddGroup(targetGroup);

                    if (existingNetwork == null)
                    {
                        try
                        {
                            targetGroup.Id = await networkService.CreateNetworkAsync(networkName, "bridge");
                            targetGroup.IsDockerConnected = true;
                        }
                        catch (Exception ex)
                        {
                            if (!ex.Message.Contains("already exists") && !ex.Message.Contains("409"))
                                throw;

                            DockerNetworkGroup? racedNetwork = (await networkService.GetNetworksAsync())
                                .FirstOrDefault(network => string.Equals(network.Name, networkName, StringComparison.OrdinalIgnoreCase));
                            targetGroup.Id = racedNetwork?.Id ?? string.Empty;
                            targetGroup.External = true;
                            targetGroup.IsDockerConnected = true;
                        }
                    }
                }

                x = targetGroup.X + 20;
                y = targetGroup.Y + 40 + (targetGroup.ContainedNodes.Count * 100);

                if (y + 80 > targetGroup.Y + targetGroup.Height)
                {
                    targetGroup.Height = (y - targetGroup.Y) + 100;
                }
            }

            var node = new NodeViewModel(containerService, volumeService, _dialogService)
            {
                Name = $"{name} (Creating...)",
                ImageName = $"{image}:{tag}",
                Type = NodeType.Container,
                X = x,
                Y = y,
                IsCreating = true,
                StatusColor = "#FFC107"
            };
            node.SetCreationProgress("Waiting to pull image...");
            creationSheet.Nodes.Add(node);
            Func<Task> retryContainerCreation = async () =>
            {
                await CreateNewContainerNodeAsync(
                    name,
                    image,
                    tag,
                    safePorts.ToList(),
                    safeEnvs.ToList(),
                    safeVolumes.ToList(),
                    restartPolicy,
                    memoryMb,
                    cpuCount,
                    x,
                    y,
                    networkName,
                    command,
                    tty,
                    regUser,
                    regPass,
                    regServer,
                    creationSheet);
            };

            try
            {
                try
                {
                    var tracker = new DockerPullProgressTracker();
                    var progress = new Progress<JSONMessage>(message =>
                    {
                        var snapshot = tracker.Update(message);
                        node.SetCreationProgress(snapshot.Message, snapshot.Percent);
                        node.StatusColor = snapshot.Percent.HasValue ? "#0D6EFD" : "#FFC107";
                    });

                    node.StatusColor = "#0D6EFD";
                    await imageService.PullImageWithProgressAsync(image, tag, progress, regUser, regPass, regServer);
                    node.SetCreationProgress("Image pull complete", 100);
                }
                catch (Exception pullEx)
                {
                    Debug.WriteLine($"[Image Pull] 원격 이미지 다운로드 실패: {pullEx.Message}");
                    var localImages = await imageService.GetImagesAsync();
                    bool existsLocally = localImages.Any(img => img.Repository == image && (img.Tag == tag || tag == "latest"));
                    if (!existsLocally)
                    {
                        node.MarkCreationFailed(
                            $"이미지 '{image}:{tag}'를 다운로드할 수 없으며 로컬에도 없습니다.\n\n{pullEx.Message}",
                            retryContainerCreation);
                        return;
                    }

                    node.SetCreationProgress("Using local image");
                }

                node.StatusColor = "#FFC107";
                node.SetCreationProgress("Creating container...");
                string containerId = await containerService.CreateAndStartContainerAsync(
                    name, image, tag, safePorts, safeEnvs, safeVolumes, restartPolicy, memoryMb, cpuCount, command, tty);

                node.Name = name;
                node.ContainerId = containerId;
                node.PortInfo = string.Join(", ", safePorts);
                node.PortBindings = safePorts;
                node.EnvironmentVariables = safeEnvs;
                node.RestartPolicy = restartPolicy;
                node.ClearCreationFailure();
                node.IsCreating = false;
                node.StatusColor = "#28a745";
                node.CreationProgressValue = 100;
                node.CreationProgressMessage = "Created";
                node.IsDockerConnected = true;

                if (targetGroup != null)
                {
                    await targetGroup.AddNodeAsync(node);
                    creationSheet.UpdateGroupLayering();
                }

                Explorer.RegisterTemplateUsage($"{image}:{tag}");

                int volIndex = 0;
                foreach (var volStr in namedVolumesToDraw)
                {
                    string volName = volStr;
                    string mountPath = "/data";

                    int lastColon = volStr.LastIndexOf(':');
                    if (lastColon > 0)
                    {
                        volName = volStr.Substring(0, lastColon);
                        mountPath = volStr.Substring(lastColon + 1);
                    }

                    var existingVolNode = creationSheet.Nodes.FirstOrDefault(n =>
                        n.Type == NodeType.Volume &&
                        (string.Equals(n.Name, volName, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(n.EffectiveVolumeName, volName, StringComparison.OrdinalIgnoreCase)));
                    NodeViewModel targetVolNode;

                    if (existingVolNode != null) targetVolNode = existingVolNode;
                    else
                    {
                        targetVolNode = new NodeViewModel(containerService, volumeService, _dialogService)
                        {
                            Name = volName,
                            Type = NodeType.Volume,
                            ImageName = "local",
                            X = x + 250,
                            Y = y + (volIndex * 100),
                            StatusColor = "#E67E22",
                            IsDockerConnected = true,
                            DockerVolumeName = volName,
                            VolumeExternal = !existingVolumes.IsReliable || existingVolumes.Names.Contains(volName)
                        };
                        creationSheet.Nodes.Add(targetVolNode);
                    }

                    bool connExists = creationSheet.Connectors.Any(c =>
                        (c.Source == node && c.Target == targetVolNode) || (c.Source == targetVolNode && c.Target == node));

                    if (!connExists)
                    {
                        var conn = new ConnectorViewModel(node, targetVolNode, PortDirection.Right, PortDirection.Left, _dialogService)
                        {
                            RelationType = RelationType.VolumeMount,
                            MountPath = mountPath
                        };
                        creationSheet.Connectors.Add(conn);
                    }
                    volIndex++;
                }
                if (ReferenceEquals(ActiveSheet, creationSheet)) Explorer.UpdateAvailableItems();
                RecordAdditionsFromSnapshot(creationSheet, historyBefore, $"Create container {name}", History.IncludeDockerResourceHistory);
            }
            catch (Exception ex)
            {
                node.MarkCreationFailed($"컨테이너 생성 중 오류가 발생했습니다:\n{ex.Message}", retryContainerCreation);
                _dialogService.ShowError($"컨테이너 생성 중 오류가 발생했습니다:\n{ex.Message}", "생성 실패");
            }
        }

        private readonly record struct ExistingVolumeSnapshot(HashSet<string> Names, bool IsReliable);

        private static async Task<ExistingVolumeSnapshot> GetExistingVolumeNamesAsync(IVolumeService volumeService)
        {
            try
            {
                HashSet<string> names = (await volumeService.GetVolumesAsync())
                    .Select(volume => volume.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return new ExistingVolumeSnapshot(names, IsReliable: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DockerDiscovery] 기존 볼륨 소유권 조회 실패: {ex.Message}");
                return new ExistingVolumeSnapshot(
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    IsReliable: false);
            }
        }

        private static string ApplyContainerNameToCliCommand(string cliCommand, string containerName)
        {
            var nameOption = new System.Text.RegularExpressions.Regex(
                @"(?<!\S)--name(?:(?:\s*=\s*)|\s+)(?:""[^""]*""|'[^']*'|\S+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (nameOption.IsMatch(cliCommand))
                return nameOption.Replace(cliCommand, $"--name {containerName}", 1);

            var runCommand = new System.Text.RegularExpressions.Regex(
                @"\bdocker\s+run\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return runCommand.IsMatch(cliCommand)
                ? runCommand.Replace(cliCommand, match => $"{match.Value} --name {containerName}", 1)
                : cliCommand;
        }
        private readonly record struct DockerCliExecutionResult(int ExitCode, string StandardOutput, string StandardError);

        internal static List<string> ParseDockerRunCommand(string cliCommand)
        {
            var regex = new System.Text.RegularExpressions.Regex("\"[^\"]*\"|'[^']*'|\\S+");
            List<string> tokens = regex.Matches(cliCommand)
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(match => match.Value.Trim('\"', '\''))
                .ToList();

            if (tokens.Count < 3 ||
                !(tokens[0].Equals("docker", StringComparison.OrdinalIgnoreCase) ||
                  tokens[0].Equals("docker.exe", StringComparison.OrdinalIgnoreCase)) ||
                !tokens[1].Equals("run", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("CLI 입력에는 단일 'docker run ...' 명령만 사용할 수 있습니다.");
            }

            return tokens;
        }

        private static async Task<DockerCliExecutionResult> ExecuteDockerCliCommandAsync(
            IReadOnlyList<string> tokens,
            ConnectionProfile profile)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "docker.exe",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in tokens.Skip(1))
                startInfo.ArgumentList.Add(argument);
            DockerCliTargetEnvironment.Apply(startInfo, profile);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("docker.exe 프로세스를 시작할 수 없습니다.");
            Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();
            string standardOutput = await standardOutputTask;
            string standardError = await standardErrorTask;
            return new DockerCliExecutionResult(process.ExitCode, standardOutput, standardError);
        }

        private static string ExtractDockerContainerId(string standardOutput)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(
                standardOutput ?? string.Empty,
                @"(?im)^[0-9a-f]{12,64}\s*$");

            return matches.Count == 0
                ? string.Empty
                : matches[^1].Value.Trim();
        }
        public async Task ProcessCliCommandAsync(string cliCommand, double x, double y, SheetViewModel? targetSheet = null)
        {
            SheetViewModel? creationSheet = targetSheet ?? ActiveSheet;
            if (creationSheet == null) return;
            IDockerService dockerService = creationSheet.DockerService;
            IContainerService containerService = dockerService;
            IVolumeService volumeService = dockerService;
            INetworkService networkService = dockerService;
            var historyBefore = CaptureDiagramState(creationSheet);
            List<string> tokens;
            try
            {
                tokens = ParseDockerRunCommand(cliCommand);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(ex.Message, "CLI 입력 오류");
                return;
            }

            ExistingVolumeSnapshot existingVolumes = await GetExistingVolumeNamesAsync(volumeService);

            string name = $"cli-{Guid.NewGuid().ToString().Substring(0, 4)}";

            string networkName = "bridge";

            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i];

                if (token.Equals("--name", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
                {
                    name = tokens[++i];
                    continue;
                }

                if (token.StartsWith("--name=", StringComparison.OrdinalIgnoreCase))
                {
                    name = token[("--name=".Length)..];
                    continue;
                }

                if ((token.Equals("--network", StringComparison.OrdinalIgnoreCase) ||
                     token.Equals("--net", StringComparison.OrdinalIgnoreCase)) &&
                    i + 1 < tokens.Count)
                {
                    networkName = tokens[++i];
                    continue;
                }

                if (token.StartsWith("--network=", StringComparison.OrdinalIgnoreCase))
                {
                    networkName = token[("--network=".Length)..];
                    continue;
                }

            }

            string? resolvedName = await _resourceNames.ResolveContainerNameAsync(creationSheet, containerService, name);
            if (resolvedName == null) return;
            name = resolvedName;
            cliCommand = ApplyContainerNameToCliCommand(cliCommand, name);
            tokens = ParseDockerRunCommand(cliCommand);

            if (networkName != "bridge" && networkName != "host" && networkName != "none")
            {
                var existingNetworks = await networkService.GetNetworksAsync();
                if (!existingNetworks.Any(n => n.Name == networkName))
                {
                    _dialogService.ShowError($"명령어 실행 실패!\n\n도커 엔진에 '{networkName}' 네트워크가 존재하지 않습니다.\n먼저 해당 네트워크를 생성한 후 다시 시도해 주세요.", "네트워크 없음");
                    return;
                }
            }

            GroupViewModel? targetGroup = null;
            if (!string.IsNullOrWhiteSpace(networkName) && networkName != "bridge" && networkName != "host" && networkName != "none")
            {
                targetGroup = creationSheet.Groups.FirstOrDefault(g => g.Type == GroupType.Network && g.Title == networkName);
                if (targetGroup == null)
                {
                    DockerNetworkGroup? existingNetwork = (await networkService.GetNetworksAsync())
                        .FirstOrDefault(network => string.Equals(network.Name, networkName, StringComparison.OrdinalIgnoreCase));
                    targetGroup = new GroupViewModel(x, y, 220, 150, networkService, _dialogService, networkName, GroupType.Network)
                    {
                        Id = existingNetwork?.Id ?? string.Empty,
                        Driver = string.IsNullOrWhiteSpace(existingNetwork?.Driver) ? "bridge" : existingNetwork.Driver,
                        External = true,
                        IsDockerConnected = true
                    };
                    creationSheet.AddGroup(targetGroup);
                }
                x = targetGroup.X + 20;
                y = targetGroup.Y + 40 + (targetGroup.ContainedNodes.Count * 100);
            }

            var dummyNode = new NodeViewModel(containerService, volumeService, _dialogService)
            {
                Name = $"{name} (Creating...)",
                ImageName = "Docker CLI",
                Type = NodeType.Container,
                X = x,
                Y = y,
                IsCreating = true,
                StatusColor = "#FFC107"
            };
            dummyNode.SetCreationProgress("Running docker command...");
            creationSheet.Nodes.Add(dummyNode);
            Func<Task> retryCliCreation = async () =>
            {
                await ProcessCliCommandAsync(cliCommand, x, y, creationSheet);
            };

            if (targetGroup != null)
            {
                await targetGroup.AddNodeAsync(dummyNode, isRestoring: true);
            }

            try
            {
                DockerCliExecutionResult execution = await ExecuteDockerCliCommandAsync(tokens, creationSheet.Profile);
                if (execution.ExitCode != 0)
                {
                    string dockerError = string.IsNullOrWhiteSpace(execution.StandardError)
                        ? execution.StandardOutput.Trim()
                        : execution.StandardError.Trim();
                    string failureMessage = string.IsNullOrWhiteSpace(dockerError)
                        ? $"Docker CLI가 종료 코드 {execution.ExitCode}을(를) 반환했습니다."
                        : dockerError;

                    _dialogService.ShowError($"명령어 실행 실패:\n{failureMessage}", "실패");
                    dummyNode.MarkCreationFailed($"명령어 실행 실패:\n{failureMessage}", retryCliCreation);
                    return;
                }

                string outputContainerId = ExtractDockerContainerId(execution.StandardOutput);
                DockerContainer? realContainer = null;
                for (int attempt = 0; attempt < 5 && realContainer == null; attempt++)
                {
                    var allContainers = await containerService.GetContainersAsync();
                    realContainer = allContainers.FirstOrDefault(container =>
                        (!string.IsNullOrWhiteSpace(outputContainerId) &&
                         (container.Id.Equals(outputContainerId, StringComparison.OrdinalIgnoreCase) ||
                          container.Id.StartsWith(outputContainerId, StringComparison.OrdinalIgnoreCase) ||
                          outputContainerId.StartsWith(container.Id, StringComparison.OrdinalIgnoreCase))) ||
                        container.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

                    if (realContainer == null)
                        await Task.Delay(200);
                }
                if (realContainer != null)
                {
                    name = realContainer.Name.TrimStart('/');
                    dummyNode.ContainerId = realContainer.Id;
                    dummyNode.Name = name;
                    dummyNode.ImageName = realContainer.Image;
                    dummyNode.ClearCreationFailure();
                    dummyNode.IsCreating = false;
                    dummyNode.StatusColor = "#28a745";
                    dummyNode.CreationProgressValue = 100;
                    dummyNode.CreationProgressMessage = "Created";
                    dummyNode.IsDockerConnected = true;

                    await dummyNode.RefreshDetailsAsync();

                    try
                    {
                        var inspectData = await containerService.InspectContainerAsync(realContainer.Id);
                        if (inspectData?.Mounts != null)
                        {
                            int volIndex = 0;
                            foreach (var mount in inspectData.Mounts)
                            {
                                if (mount.Type == "volume")
                                {
                                    string volName = mount.Name;
                                    string mountPath = mount.Destination;

                                    var existingVolNode = creationSheet.Nodes.FirstOrDefault(n =>
                                        n.Type == NodeType.Volume &&
                                        (string.Equals(n.Name, volName, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(n.EffectiveVolumeName, volName, StringComparison.OrdinalIgnoreCase)));
                                    NodeViewModel targetVolNode;

                                    if (existingVolNode != null) targetVolNode = existingVolNode;
                                    else
                                    {
                                        targetVolNode = new NodeViewModel(containerService, volumeService, _dialogService)
                                        {
                                            Name = volName,
                                            Type = NodeType.Volume,
                                            ImageName = "local",
                                            X = dummyNode.X + 250,
                                            Y = dummyNode.Y + (volIndex * 100),
                                            StatusColor = "#E67E22",
                                            IsDockerConnected = true,
                                            DockerVolumeName = volName,
                                            VolumeExternal = !existingVolumes.IsReliable || existingVolumes.Names.Contains(volName)
                                        };
                                        creationSheet.Nodes.Add(targetVolNode);
                                    }

                                    bool connExists = creationSheet.Connectors.Any(c =>
                                        (c.Source == dummyNode && c.Target == targetVolNode) || (c.Source == targetVolNode && c.Target == dummyNode));

                                    if (!connExists)
                                    {
                                        var conn = new ConnectorViewModel(dummyNode, targetVolNode, PortDirection.Right, PortDirection.Left, _dialogService)
                                        {
                                            RelationType = RelationType.VolumeMount,
                                            MountPath = mountPath
                                        };
                                        creationSheet.Connectors.Add(conn);
                                    }
                                    volIndex++;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[DockerDiscovery] Inspect 실패: {ex.Message}");

                        _dialogService.ShowInfo(
                            $"컨테이너 '{name}'(은)는 성공적으로 생성되었으나, 볼륨 마운트 등의 상세 정보를 불러오는데 실패했습니다.\n" +
                            $"컨테이너가 실행 직후 즉시 종료(Exit)되었거나 API 응답이 지연되었을 수 있습니다.\n\n" +
                            $"[상세 오류]\n{ex.Message}",
                            "⚠️ 상세 정보 동기화 경고"
                        );
                    }

                    if (ReferenceEquals(ActiveSheet, creationSheet)) Explorer.UpdateAvailableItems();
                    RecordAdditionsFromSnapshot(creationSheet, historyBefore, $"Create container {name}", History.IncludeDockerResourceHistory);
                }
                else
                {
                    _dialogService.ShowError($"Docker CLI는 성공했지만 생성된 컨테이너를 찾지 못했습니다.\n\nDocker 출력:\n{execution.StandardOutput.Trim()}", "동기화 실패");
                    dummyNode.MarkCreationFailed(
                        $"Docker CLI는 성공했지만 생성된 컨테이너를 찾지 못했습니다.\n\nDocker 출력:\n{execution.StandardOutput.Trim()}",
                        retryCliCreation);
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"CMD 실행 중 오류가 발생했습니다:\n{ex.Message}", "명령어 실행 오류");
                dummyNode.MarkCreationFailed($"CMD 실행 중 오류가 발생했습니다:\n{ex.Message}", retryCliCreation);
            }
        }

        public async Task BuildImageAndCreateNodeAsync(string targetImageName, string dockerfileContent, string uploadedFilePath, double x, double y, SheetViewModel? targetSheet = null)
        {
            SheetViewModel? creationSheet = targetSheet ?? ActiveSheet;
            if (creationSheet == null) return;
            IImageService imageService = creationSheet.DockerService;
            IContainerService containerService = creationSheet.DockerService;
            IVolumeService volumeService = creationSheet.DockerService;
            if (string.IsNullOrWhiteSpace(targetImageName)) targetImageName = $"custom-app:{Guid.NewGuid().ToString().Substring(0, 4)}";

            string buildContextPath = "";
            string dockerfilePath = "";

            if (!string.IsNullOrEmpty(uploadedFilePath) && System.IO.File.Exists(uploadedFilePath))
            {
                dockerfilePath = uploadedFilePath;
                buildContextPath = Path.GetDirectoryName(uploadedFilePath)
                    ?? throw new InvalidOperationException("Dockerfile 경로의 상위 폴더를 확인할 수 없습니다.");
            }
            else
            {
                buildContextPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DockerDiagramBuild_" + Guid.NewGuid().ToString().Substring(0, 8));
                System.IO.Directory.CreateDirectory(buildContextPath);
                dockerfilePath = System.IO.Path.Combine(buildContextPath, "Dockerfile");
                await System.IO.File.WriteAllTextAsync(dockerfilePath, dockerfileContent);
            }

            var dummyNode = new NodeViewModel(containerService, volumeService, _dialogService)
            {
                Name = $"Building ({targetImageName})...",
                ImageName = "Building...",
                Type = NodeType.Container,
                X = x,
                Y = y,
                IsCreating = true,
                StatusColor = "#17a2b8"
            };
            dummyNode.SetCreationProgress("Building image...");
            creationSheet.Nodes.Add(dummyNode);
            Func<Task> retryBuildCreation = async () =>
            {
                await BuildImageAndCreateNodeAsync(targetImageName, dockerfileContent, uploadedFilePath, x, y, creationSheet);
            };

            try
            {
                await imageService.BuildImageAsync(targetImageName, buildContextPath, dockerfilePath);

                creationSheet.Nodes.Remove(dummyNode);

                string containerName = targetImageName.Split(':')[0] + "-" + Guid.NewGuid().ToString().Substring(0, 4);

                await CreateNewContainerNodeAsync(
                    containerName, targetImageName.Split(':')[0],
                    targetImageName.Contains(":") ? targetImageName.Split(':')[1] : "latest",
                    new List<string>(), new List<string>(), new List<string>(), "no", 0, 0, x, y,
                    targetSheet: creationSheet);
            }
            catch (Exception ex)
            {
                dummyNode.MarkCreationFailed($"빌드 중 오류 발생:\n{ex.Message}", retryBuildCreation);
                _dialogService.ShowMessage($"빌드 중 오류 발생: {ex.Message}");
            }
        }

        public async Task BuildImageOnlyAsync(string targetImageName, string dockerfileContent, string uploadedFilePath)
        {
            SheetViewModel? creationSheet = ActiveSheet;
            if (creationSheet == null) return;
            IImageService imageService = creationSheet.DockerService;
            if (string.IsNullOrWhiteSpace(targetImageName))
            {
                targetImageName = $"custom-image:{Guid.NewGuid().ToString().Substring(0, 4)}";
            }

            string buildContextPath = "";
            string dockerfilePath = "";
            bool isTempContext = false;

            try
            {
                _dialogService.SetBusyCursor(true);
                _dialogService.ShowConfirm($"[{targetImageName}] 이미지 빌드를 시작합니다...\n(백그라운드에서 진행됩니다.)", "빌드 시작");

                if (!string.IsNullOrEmpty(uploadedFilePath) && File.Exists(uploadedFilePath))
                {
                    dockerfilePath = uploadedFilePath;
                    buildContextPath = Path.GetDirectoryName(uploadedFilePath)
                        ?? throw new InvalidOperationException("Dockerfile 경로의 상위 폴더를 확인할 수 없습니다.");
                }
                else
                {
                    isTempContext = true;
                    buildContextPath = Path.Combine(Path.GetTempPath(), "DockerDiagramBuild_" + Guid.NewGuid().ToString().Substring(0, 8));
                    Directory.CreateDirectory(buildContextPath);

                    dockerfilePath = Path.Combine(buildContextPath, "Dockerfile");
                    await File.WriteAllTextAsync(dockerfilePath, dockerfileContent);
                }

                await imageService.BuildImageAsync(targetImageName, buildContextPath, dockerfilePath);

                _dialogService.ShowConfirm($"[{targetImageName}] 이미지가 성공적으로 생성되었습니다!", "빌드 완료");
                if (ReferenceEquals(ActiveSheet, creationSheet))
                    await RefreshRuntimeResourcesAsync();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"빌드 중 시스템 오류 발생: {ex.Message}", "오류");
            }
            finally
            {
                _dialogService.SetBusyCursor(false);
                if (isTempContext && Directory.Exists(buildContextPath))
                {
                    try { Directory.Delete(buildContextPath, true); } catch { }
                }
            }
        }
    }
}
