using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Docker.DotNet.Models;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Diagram;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    public partial class MainViewModel
    {
        private const double GroupNodeLeftInset = 20;
        private const double GroupNodeTopInset = 40;
        private const double GroupNodeVerticalGap = 20;

        public async Task CreateNewContainerNodeAsync(string name, string image, string tag, List<string> ports, List<string> envs, IReadOnlyList<ContainerMountSpec> mounts, string restartPolicy, long memoryMb, double cpuCount, double x, double y, string networkName = DockerNetworkSafety.Bridge, string command = "", bool tty = false, string? regUser = null, string? regPass = null, string? regServer = null, SheetViewModel? targetSheet = null)
        {
            SheetViewModel? creationSheet = targetSheet ?? ActiveSheet;
            if (creationSheet == null) return;
            IDockerService dockerService = creationSheet.DockerService;
            IContainerService containerService = dockerService;
            IVolumeService volumeService = dockerService;
            INetworkService networkService = dockerService;
            IImageService imageService = dockerService;
            var historyBefore = CaptureDiagramState(creationSheet);
            var volumeNodesBeforeCreation = new HashSet<NodeViewModel>(
                creationSheet.Nodes.Where(existing => existing.Type == NodeType.Volume),
                ReferenceEqualityComparer.Instance);
            var safePorts = ports ?? [];
            var safeEnvs = envs ?? [];
            var safeMounts = mounts?.Select(mount => mount.Clone()).ToList() ?? [];

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

            var namedVolumesToDraw = new List<(string VolumeName, string MountPath)>();
            foreach (ContainerMountSpec mount in safeMounts.Where(mount => mount.Kind == MountKind.Volume))
            {
                if (!string.IsNullOrWhiteSpace(mount.Source))
                    namedVolumesToDraw.Add((mount.Source, mount.Target));
            }

            ExistingVolumeSnapshot existingVolumes = await GetExistingVolumeNamesAsync(volumeService);

            (image, tag) = DockerImageReferenceParser.Split(image, tag);

            GroupViewModel? targetGroup = null;

            if (DockerNetworkSafety.ShouldCreateDiagramGroup(networkName))
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
                        AppLog.Warning("기존 네트워크 소유권 조회 실패", ex);
                    }
                    targetGroup = new GroupViewModel(x, y, 220, 150, networkService, _dialogService, networkName, GroupType.Network)
                    {
                        Id = existingNetwork?.Id ?? string.Empty,
                        Driver = string.IsNullOrWhiteSpace(existingNetwork?.Driver) ? DockerNetworkSafety.Bridge : existingNetwork.Driver,
                        External = existingNetwork != null,
                        IsDockerConnected = existingNetwork != null
                    };
                    creationSheet.AddGroup(targetGroup);

                    if (existingNetwork == null)
                    {
                        try
                        {
                            targetGroup.Id = await networkService.CreateNetworkAsync(networkName, DockerNetworkSafety.Bridge);
                            targetGroup.IsDockerConnected = true;
                        }
                        catch (Exception ex)
                        {
                            if (!DockerErrorClassifier.IsConflict(ex))
                                throw;

                            DockerNetworkGroup? racedNetwork = (await networkService.GetNetworksAsync())
                                .FirstOrDefault(network => string.Equals(network.Name, networkName, StringComparison.OrdinalIgnoreCase));
                            targetGroup.Id = racedNetwork?.Id ?? string.Empty;
                            targetGroup.External = true;
                            targetGroup.IsDockerConnected = true;
                        }
                    }
                }

                (x, y) = GetNextNodePositionInGroup(targetGroup);
            }

            NodeViewModel node = DiagramNodeFactory.CreatePendingContainer(
                containerService,
                volumeService,
                _dialogService,
                $"{name} (Creating...)",
                $"{image}:{tag}",
                x,
                y);
            node.SetCreationProgress(
                NodeOperationPhase.Preparing,
                "이미지 다운로드 준비 중",
                detail: $"{image}:{tag} 이미지의 로컬 상태와 원격 레이어 정보를 확인합니다.");
            creationSheet.Nodes.Add(node);
            Func<Task> retryContainerCreation = async () =>
            {
                await CreateNewContainerNodeAsync(
                    name,
                    image,
                    tag,
                    safePorts.ToList(),
                    safeEnvs.ToList(),
                    safeMounts.Select(mount => mount.Clone()).ToList(),
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
                        node.SetCreationProgress(
                            NodeOperationPhase.Pulling,
                            snapshot.Message,
                            snapshot.DisplayPercent,
                            snapshot.DetailMessage);
                        node.StatusColor = snapshot.DisplayPercent.HasValue ? StatusColors.Progress : StatusColors.InProgress;
                    });

                    node.StatusColor = StatusColors.Progress;
                    await imageService.PullImageWithProgressAsync(
                        image,
                        tag,
                        progress,
                        regUser,
                        regPass,
                        regServer,
                        LifetimeToken);
                    node.SetCreationProgress(
                        NodeOperationPhase.Pulling,
                        "이미지 다운로드 완료",
                        100,
                        $"{image}:{tag} 이미지의 모든 필요한 레이어가 준비되었습니다.");
                }
                catch (OperationCanceledException) when (LifetimeToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception pullEx)
                {
                    AppLog.Warning("원격 이미지 다운로드 실패; 로컬 이미지 존재 여부를 확인합니다.", pullEx);
                    var localImages = await imageService.GetImagesAsync();
                    bool existsLocally = localImages.Any(img => img.Repository == image && (img.Tag == tag || tag == "latest"));
                    if (!existsLocally)
                    {
                        node.MarkCreationFailed(
                            $"이미지 '{image}:{tag}'를 다운로드할 수 없으며 로컬에도 없습니다.\n\n{pullEx.Message}",
                            retryContainerCreation);
                        return;
                    }

                    node.SetCreationProgress(
                        NodeOperationPhase.Preparing,
                        "로컬 이미지 사용",
                        detail: $"원격 다운로드 대신 로컬에 있는 {image}:{tag} 이미지를 사용합니다.");
                }

                node.StatusColor = StatusColors.InProgress;
                node.SetCreationProgress(
                    NodeOperationPhase.Creating,
                    "컨테이너 생성 준비 중",
                    detail: "이미지 준비가 끝났습니다. 컨테이너 설정을 Docker 요청으로 변환합니다.");
                var creationProgress = new Progress<ContainerCreationProgress>(stage =>
                {
                    node.SetCreationProgress(
                        stage.Phase switch
                        {
                            ContainerCreationPhase.Preparing => NodeOperationPhase.Preparing,
                            ContainerCreationPhase.Creating => NodeOperationPhase.Creating,
                            ContainerCreationPhase.Starting => NodeOperationPhase.Starting,
                            ContainerCreationPhase.Started => NodeOperationPhase.Synchronizing,
                            _ => NodeOperationPhase.Creating
                        },
                        stage.Message,
                        detail: stage.Detail);
                    node.StatusColor = stage.Phase == ContainerCreationPhase.Started
                        ? StatusColors.Progress
                        : StatusColors.InProgress;
                });
                string containerId = await containerService.CreateAndStartContainerAsync(
                    name, image, tag, safePorts, safeEnvs, safeMounts, restartPolicy, memoryMb, cpuCount,
                    command, tty, networkName, progress: creationProgress);

                node.Name = name;
                node.ContainerId = containerId;
                node.PortInfo = string.Join(", ", safePorts);
                node.PortBindings = safePorts;
                node.EnvironmentVariables = safeEnvs;
                node.RestartPolicy = restartPolicy;
                node.ClearCreationFailure();
                node.SetDockerConnection(true);
                node.StatusColor = StatusColors.Progress;
                node.SetCreationProgress(
                    NodeOperationPhase.Synchronizing,
                    "컨테이너 정보 동기화 중",
                    detail: "실제 실행 상태, 네트워크 주소, 포트 및 볼륨 마운트 정보를 불러옵니다.");

                if (targetGroup != null)
                {
                    // 컨테이너가 생성 단계에서 이미 이 네트워크에 연결되었으므로
                    // Docker connect를 다시 호출하지 않고 시트 포함 관계만 반영합니다.
                    await targetGroup.AddNodeAsync(node, isRestoring: true);
                    creationSheet.UpdateGroupLayering();
                }

                // 생성 입력만으로는 Dockerfile의 VOLUME이나 Docker가 자동으로 만든
                // 익명 볼륨을 알 수 없으므로 실제 컨테이너 정보를 즉시 동기화합니다.
                await node.RefreshDetailsAsync(refreshMonitoring: false);
                creationSheet.SynchronizeContainerMounts(node, node.MountSpecifications);
                ApplyNewVolumeOwnership(
                    creationSheet,
                    node.MountSpecifications,
                    volumeNodesBeforeCreation,
                    existingVolumes);
                foreach (MountDisplayItem mount in node.ContainerOperations.NamedVolumeItems)
                {
                    if (string.IsNullOrWhiteSpace(mount.Source)) continue;

                    bool alreadyIncluded = namedVolumesToDraw.Any(candidate =>
                        string.Equals(candidate.VolumeName, mount.Source, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(candidate.MountPath, mount.Target, StringComparison.Ordinal));
                    if (!alreadyIncluded)
                        namedVolumesToDraw.Add((mount.Source, mount.Target));
                }

                Explorer.RegisterTemplateUsage($"{image}:{tag}");

                int volIndex = 0;
                foreach ((string volName, string mountPath) in namedVolumesToDraw)
                {
                    var existingVolNode = creationSheet.Nodes.FirstOrDefault(n =>
                        n.Type == NodeType.Volume &&
                        (string.Equals(n.Name, volName, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(n.EffectiveVolumeName, volName, StringComparison.OrdinalIgnoreCase)));
                    NodeViewModel targetVolNode;

                    if (existingVolNode != null) targetVolNode = existingVolNode;
                    else
                    {
                        targetVolNode = DiagramNodeFactory.CreateVolume(
                            containerService,
                            volumeService,
                            _dialogService,
                            volName,
                            x + 250,
                            y + (volIndex * 100),
                            isConnected: true);
                        targetVolNode.ImageName = "local";
                        targetVolNode.Driver = "local";
                        targetVolNode.DockerVolumeName = volName;
                        targetVolNode.VolumeExternal = !existingVolumes.IsReliable || existingVolumes.Names.Contains(volName);
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

                node.SetCreationProgress(
                    NodeOperationPhase.Completed,
                    "생성 완료",
                    100,
                    "이미지 다운로드, 컨테이너 생성·시작 및 상세 정보 동기화가 완료되었습니다.");
                if (ReferenceEquals(ActiveSheet, creationSheet)) Explorer.UpdateAvailableItems();
                RecordAdditionsFromSnapshot(creationSheet, historyBefore, $"Create container {name}", History.IncludeDockerResourceHistory);
            }
            catch (OperationCanceledException) when (LifetimeToken.IsCancellationRequested)
            {
                if (creationSheet.Nodes.Remove(node))
                    node.Dispose();
                AppLog.Info($"컨테이너 생성이 애플리케이션 종료로 취소되었습니다: {name}");
            }
            catch (Exception ex)
            {
                node.MarkCreationFailed($"컨테이너 생성 중 오류가 발생했습니다:\n{ex.Message}", retryContainerCreation);
                _dialogService.ShowError($"컨테이너 생성 중 오류가 발생했습니다:\n{ex.Message}", "생성 실패");
            }
        }

        private readonly record struct ExistingVolumeSnapshot(HashSet<string> Names, bool IsReliable);

        private static void ApplyNewVolumeOwnership(
            SheetViewModel sheet,
            IEnumerable<ContainerMountSpec> mounts,
            HashSet<NodeViewModel> volumeNodesBeforeCreation,
            ExistingVolumeSnapshot existingVolumes)
        {
            foreach (string volumeName in mounts
                         .Where(mount => mount.Kind == MountKind.Volume && !string.IsNullOrWhiteSpace(mount.Source))
                         .Select(mount => mount.Source)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                NodeViewModel? volumeNode = sheet.Nodes.FirstOrDefault(candidate =>
                    candidate.Type == NodeType.Volume &&
                    (string.Equals(candidate.Name, volumeName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(candidate.EffectiveVolumeName, volumeName, StringComparison.OrdinalIgnoreCase)));
                if (volumeNode == null || volumeNodesBeforeCreation.Contains(volumeNode))
                    continue;

                volumeNode.VolumeExternal = !existingVolumes.IsReliable ||
                                            existingVolumes.Names.Contains(volumeName);
            }
        }

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
                AppLog.Warning("기존 볼륨 소유권 조회 실패", ex);
                return new ExistingVolumeSnapshot(
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    IsReliable: false);
            }
        }

        private static string ApplyContainerNameToCliCommand(string cliCommand, string containerName)
        {
            var nameOption = DockerNameOptionRegex();

            if (nameOption.IsMatch(cliCommand))
                return nameOption.Replace(cliCommand, $"--name {containerName}", 1);

            var runCommand = DockerRunCommandRegex();
            return runCommand.IsMatch(cliCommand)
                ? runCommand.Replace(cliCommand, match => $"{match.Value} --name {containerName}", 1)
                : cliCommand;
        }
        private readonly record struct DockerCliExecutionResult(int ExitCode, string StandardOutput, string StandardError);

        internal static List<string> ParseDockerRunCommand(string cliCommand)
        {
            List<string> tokens = DockerCliTokenRegex().Matches(cliCommand)
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
            ConnectionProfile profile,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            TimeSpan commandTimeout = ContainerCreationTiming.CliCommandTimeout;
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
            using var timeout = new CancellationTokenSource(commandTimeout);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            Task<string> standardOutputTask = ReadProcessStreamAsync(
                process.StandardOutput,
                progress,
                operation.Token);
            Task<string> standardErrorTask = ReadProcessStreamAsync(
                process.StandardError,
                progress,
                operation.Token);

            try
            {
                await process.WaitForExitAsync(operation.Token);
                await Task.WhenAll(standardOutputTask, standardErrorTask);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception killException)
                {
                    AppLog.Warning("취소된 docker run 프로세스 종료 실패", killException);
                }

                if (cancellationToken.IsCancellationRequested)
                    throw;

                throw new TimeoutException(
                    $"docker run이 {commandTimeout.TotalMinutes:0}분 안에 완료되지 않아 중단했습니다.");
            }

            string standardOutput = await standardOutputTask;
            string standardError = await standardErrorTask;
            return new DockerCliExecutionResult(process.ExitCode, standardOutput, standardError);
        }

        private static async Task<string> ReadProcessStreamAsync(
            StreamReader reader,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            var output = new StringBuilder();
            var pendingLine = new StringBuilder();
            char[] buffer = new char[512];

            while (true)
            {
                int count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (count == 0)
                    break;

                output.Append(buffer, 0, count);
                for (int index = 0; index < count; index++)
                {
                    char character = buffer[index];
                    if (character is '\r' or '\n')
                    {
                        ReportPendingLine(progress, pendingLine);
                        continue;
                    }

                    pendingLine.Append(character);
                }

                if (pendingLine.Length > 0 &&
                    (pendingLine.ToString().Contains("Downloading", StringComparison.OrdinalIgnoreCase) ||
                     pendingLine.ToString().Contains("Extracting", StringComparison.OrdinalIgnoreCase)))
                {
                    progress?.Report(pendingLine.ToString());
                }
            }

            ReportPendingLine(progress, pendingLine);
            return output.ToString();
        }

        private static void ReportPendingLine(IProgress<string>? progress, StringBuilder pendingLine)
        {
            if (pendingLine.Length == 0)
                return;

            string line = pendingLine.ToString().Trim();
            pendingLine.Clear();
            if (!string.IsNullOrWhiteSpace(line))
                progress?.Report(line);
        }

        private static string ExtractDockerContainerId(string standardOutput)
        {
            var matches = DockerContainerIdRegex().Matches(standardOutput ?? string.Empty);

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
            var volumeNodesBeforeCreation = new HashSet<NodeViewModel>(
                creationSheet.Nodes.Where(existing => existing.Type == NodeType.Volume),
                ReferenceEqualityComparer.Instance);
            List<string> tokens;
            try
            {
                tokens = ParseDockerRunCommand(cliCommand);
                _ = ContainerMountParser.ParseDockerRunTokens(tokens);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(ex.Message, "CLI 입력 오류");
                return;
            }

            ExistingVolumeSnapshot existingVolumes = await GetExistingVolumeNamesAsync(volumeService);

            string name = $"cli-{Guid.NewGuid().ToString("N")[..4]}";

            string networkName = DockerNetworkSafety.Bridge;

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

            if (DockerNetworkSafety.ShouldCreateDiagramGroup(networkName))
            {
                var existingNetworks = await networkService.GetNetworksAsync();
                if (!existingNetworks.Any(n => n.Name == networkName))
                {
                    _dialogService.ShowError($"명령어 실행 실패!\n\n도커 엔진에 '{networkName}' 네트워크가 존재하지 않습니다.\n먼저 해당 네트워크를 생성한 후 다시 시도해 주세요.", "네트워크 없음");
                    return;
                }
            }

            GroupViewModel? targetGroup = null;
            if (DockerNetworkSafety.ShouldCreateDiagramGroup(networkName))
            {
                targetGroup = creationSheet.Groups.FirstOrDefault(g => g.Type == GroupType.Network && g.Title == networkName);
                if (targetGroup == null)
                {
                    DockerNetworkGroup? existingNetwork = (await networkService.GetNetworksAsync())
                        .FirstOrDefault(network => string.Equals(network.Name, networkName, StringComparison.OrdinalIgnoreCase));
                    targetGroup = new GroupViewModel(x, y, 220, 150, networkService, _dialogService, networkName, GroupType.Network)
                    {
                        Id = existingNetwork?.Id ?? string.Empty,
                        Driver = string.IsNullOrWhiteSpace(existingNetwork?.Driver) ? DockerNetworkSafety.Bridge : existingNetwork.Driver,
                        External = true,
                        IsDockerConnected = true
                    };
                    creationSheet.AddGroup(targetGroup);
                }
                (x, y) = GetNextNodePositionInGroup(targetGroup);
            }

            NodeViewModel dummyNode = DiagramNodeFactory.CreatePendingContainer(
                containerService,
                volumeService,
                _dialogService,
                $"{name} (Creating...)",
                "Docker CLI",
                x,
                y);
            dummyNode.SetCreationProgress(
                NodeOperationPhase.Preparing,
                "Docker 명령 실행 준비 중",
                detail: "docker run 명령을 시작하고 이미지 및 컨테이너 상태 출력을 기다립니다.");
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
                var cliTracker = new DockerCliPullProgressTracker();
                var cliProgress = new Progress<string>(line =>
                {
                    DockerPullProgressSnapshot snapshot = cliTracker.Update(line);
                    bool isImageProgress = snapshot.DisplayPercent.HasValue ||
                                           snapshot.ObservedLayerCount > 0 ||
                                           line.Contains("Pulling from", StringComparison.OrdinalIgnoreCase) ||
                                           line.Contains("Unable to find image", StringComparison.OrdinalIgnoreCase);
                    if (isImageProgress)
                    {
                        dummyNode.SetCreationProgress(
                            NodeOperationPhase.Pulling,
                            snapshot.Message,
                            snapshot.DisplayPercent,
                            snapshot.DetailMessage);
                        dummyNode.StatusColor = snapshot.DisplayPercent.HasValue ? StatusColors.Progress : StatusColors.InProgress;
                    }
                    else
                    {
                        dummyNode.SetCreationProgress(
                            NodeOperationPhase.Creating,
                            "Docker 명령 실행 중",
                            detail: line.Length <= 240 ? line : line[..237] + "...");
                    }
                });
                DockerCliExecutionResult execution = await ExecuteDockerCliCommandAsync(
                    tokens,
                    creationSheet.Profile,
                    cliProgress,
                    LifetimeToken);
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

                dummyNode.StatusColor = StatusColors.Progress;
                dummyNode.SetCreationProgress(
                    NodeOperationPhase.Synchronizing,
                    "컨테이너 정보 확인 중",
                    detail: "Docker 명령이 완료되었습니다. 생성된 컨테이너 ID와 실제 실행 상태를 확인합니다.");
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
                        await Task.Delay(ContainerCreationTiming.PullProgressPollInterval, LifetimeToken);
                }
                if (realContainer != null)
                {
                    name = realContainer.Name.TrimStart('/');
                    dummyNode.ContainerId = realContainer.Id;
                    dummyNode.Name = name;
                    dummyNode.ImageName = realContainer.Image;
                    dummyNode.ClearCreationFailure();
                    dummyNode.SetDockerConnection(true);
                    dummyNode.StatusColor = StatusColors.Progress;
                    dummyNode.SetCreationProgress(
                        NodeOperationPhase.Synchronizing,
                        "컨테이너 정보 동기화 중",
                        detail: "실제 실행 상태, 네트워크, 포트 및 볼륨 마운트 정보를 불러옵니다.");

                    await dummyNode.RefreshDetailsAsync();

                    try
                    {
                        var inspectData = await containerService.InspectContainerAsync(realContainer.Id);
                        if (inspectData?.Mounts != null)
                        {
                            List<ContainerMountSpec> mounts = ContainerMountMapper.FromInspect(
                                inspectData.Mounts,
                                inspectData.HostConfig?.Mounts);
                            creationSheet.SynchronizeContainerMounts(
                                dummyNode,
                                mounts);
                            ApplyNewVolumeOwnership(
                                creationSheet,
                                mounts,
                                volumeNodesBeforeCreation,
                                existingVolumes);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warning("컨테이너 inspect 실패", ex);

                        _dialogService.ShowInfo(
                            $"컨테이너 '{name}'(은)는 성공적으로 생성되었으나, 볼륨 마운트 등의 상세 정보를 불러오는데 실패했습니다.\n" +
                            $"컨테이너가 실행 직후 즉시 종료(Exit)되었거나 API 응답이 지연되었을 수 있습니다.\n\n" +
                            $"[상세 오류]\n{ex.Message}",
                            "⚠️ 상세 정보 동기화 경고"
                        );
                    }

                    dummyNode.SetCreationProgress(
                        NodeOperationPhase.Completed,
                        "생성 완료",
                        100,
                        "Docker CLI 이미지 준비와 컨테이너 생성 및 상세 정보 동기화가 완료되었습니다.");
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
            catch (OperationCanceledException) when (LifetimeToken.IsCancellationRequested)
            {
                AppLog.Info($"docker run이 애플리케이션 종료로 취소되었습니다: {name}");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"CMD 실행 중 오류가 발생했습니다:\n{ex.Message}", "명령어 실행 오류");
                dummyNode.MarkCreationFailed($"CMD 실행 중 오류가 발생했습니다:\n{ex.Message}", retryCliCreation);
            }
        }

        private static (double X, double Y) GetNextNodePositionInGroup(GroupViewModel group)
        {
            double x = group.X + GroupNodeLeftInset;
            double y = group.Y + GroupNodeTopInset +
                (group.ContainedNodes.Count * (NodeViewModel.StandardHeight + GroupNodeVerticalGap));
            double requiredHeight = (y - group.Y) + NodeViewModel.StandardHeight + GroupNodeVerticalGap;

            if (requiredHeight > group.Height)
                group.Height = requiredHeight;

            return (x, y);
        }

        public async Task BuildImageAndCreateNodeAsync(string targetImageName, string dockerfileContent, string uploadedFilePath, double x, double y, SheetViewModel? targetSheet = null)
        {
            SheetViewModel? creationSheet = targetSheet ?? ActiveSheet;
            if (creationSheet == null) return;
            IImageService imageService = creationSheet.DockerService;
            IContainerService containerService = creationSheet.DockerService;
            IVolumeService volumeService = creationSheet.DockerService;
            if (string.IsNullOrWhiteSpace(targetImageName)) targetImageName = $"custom-app:{Guid.NewGuid().ToString("N")[..4]}";

            string buildContextPath = "";
            string dockerfilePath = "";
            bool isTempContext = false;
            NodeViewModel? dummyNode = null;

            try
            {
                PreparedDockerBuildContext preparedContext = await PrepareDockerBuildContextAsync(
                    dockerfileContent,
                    uploadedFilePath,
                    LifetimeToken);
                buildContextPath = preparedContext.ContextPath;
                dockerfilePath = preparedContext.DockerfilePath;
                isTempContext = preparedContext.IsTemporary;

                dummyNode = DiagramNodeFactory.CreatePendingContainer(
                    containerService,
                    volumeService,
                    _dialogService,
                    $"Building ({targetImageName})...",
                    "Building...",
                    x,
                    y);
                dummyNode.StatusColor = StatusColors.Busy;
                dummyNode.SetCreationProgress(
                    NodeOperationPhase.Preparing,
                    preparedContext.UsesDockerfileDirectory
                        ? "빌드 컨텍스트 준비 중"
                        : "Dockerfile 빌드 준비 중",
                    detail: preparedContext.UsesDockerfileDirectory
                        ? "Dockerfile 폴더에서 필요한 파일을 빌드 컨텍스트로 묶고 있습니다."
                        : "Dockerfile을 Docker 빌드 API로 전달할 준비를 하고 있습니다.");
                creationSheet.Nodes.Add(dummyNode);
                Func<Task> retryBuildCreation = () =>
                    BuildImageAndCreateNodeAsync(targetImageName, dockerfileContent, uploadedFilePath, x, y, creationSheet);

                var buildTracker = new DockerPullProgressTracker("이미지 빌드");
                var buildProgress = new Progress<JSONMessage>(message =>
                {
                    DockerPullProgressSnapshot snapshot = buildTracker.Update(message);
                    dummyNode.SetCreationProgress(
                        NodeOperationPhase.Building,
                        snapshot.Message,
                        snapshot.DisplayPercent,
                        snapshot.DetailMessage);
                    dummyNode.StatusColor = snapshot.DisplayPercent.HasValue ? StatusColors.Progress : StatusColors.Busy;
                });
                await imageService.BuildImageAsync(
                    targetImageName,
                    buildContextPath,
                    dockerfilePath,
                    buildProgress,
                    cancellationToken: LifetimeToken);

                dummyNode.SetCreationProgress(
                    NodeOperationPhase.Building,
                    "이미지 빌드 완료",
                    100,
                    $"{targetImageName} 이미지 빌드가 완료되어 컨테이너 생성을 준비합니다.");

                if (creationSheet.Nodes.Remove(dummyNode))
                    dummyNode.Dispose();

                (string repository, string tag) = DockerImageReferenceParser.Split(targetImageName);
                string imageBaseName = repository.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "container";
                string safeName = InvalidDockerObjectNameCharacterRegex().Replace(imageBaseName, "-");
                string containerName = $"{safeName}-{Guid.NewGuid().ToString("N")[..4]}";

                await CreateNewContainerNodeAsync(
                    containerName, repository, tag,
                    [], [], [], "no", 0, 0, x, y,
                    targetSheet: creationSheet);
            }
            catch (OperationCanceledException) when (LifetimeToken.IsCancellationRequested)
            {
                if (dummyNode != null)
                    if (creationSheet.Nodes.Remove(dummyNode))
                        dummyNode.Dispose();
                AppLog.Info($"이미지 빌드가 애플리케이션 종료로 취소되었습니다: {targetImageName}");
            }
            catch (Exception ex)
            {
                if (dummyNode != null)
                {
                    if (!creationSheet.Nodes.Contains(dummyNode))
                        creationSheet.Nodes.Add(dummyNode);

                    dummyNode.MarkCreationFailed(
                        $"빌드 중 오류 발생:\n{ex.Message}",
                        () => BuildImageAndCreateNodeAsync(targetImageName, dockerfileContent, uploadedFilePath, x, y, creationSheet));
                }
                _dialogService.ShowMessage($"빌드 중 오류 발생: {ex.Message}");
            }
            finally
            {
                if (isTempContext && !string.IsNullOrWhiteSpace(buildContextPath))
                {
                    try
                    {
                        if (System.IO.Directory.Exists(buildContextPath))
                            System.IO.Directory.Delete(buildContextPath, recursive: true);
                    }
                    catch (Exception cleanupException)
                    {
                        AppLog.Warning($"임시 빌드 폴더 정리 실패: {buildContextPath}", cleanupException);
                    }
                }
            }
        }

        public async Task BuildImageOnlyAsync(string targetImageName, string dockerfileContent, string uploadedFilePath)
        {
            SheetViewModel? creationSheet = ActiveSheet;
            if (creationSheet == null) return;
            IImageService imageService = creationSheet.DockerService;
            if (string.IsNullOrWhiteSpace(targetImageName))
            {
                targetImageName = $"custom-image:{Guid.NewGuid().ToString("N")[..4]}";
            }

            string buildContextPath = "";
            string dockerfilePath = "";
            bool isTempContext = false;

            try
            {
                _dialogService.SetBusyCursor(true);
                _dialogService.ShowInfo($"[{targetImageName}] 이미지 빌드를 시작합니다...\n(백그라운드에서 진행됩니다.)", "빌드 시작");

                PreparedDockerBuildContext preparedContext = await PrepareDockerBuildContextAsync(
                    dockerfileContent,
                    uploadedFilePath,
                    LifetimeToken);
                buildContextPath = preparedContext.ContextPath;
                dockerfilePath = preparedContext.DockerfilePath;
                isTempContext = preparedContext.IsTemporary;

                await imageService.BuildImageAsync(
                    targetImageName,
                    buildContextPath,
                    dockerfilePath,
                    cancellationToken: LifetimeToken);

                _dialogService.ShowInfo($"[{targetImageName}] 이미지가 성공적으로 생성되었습니다!", "빌드 완료");
                if (ReferenceEquals(ActiveSheet, creationSheet))
                    await RefreshRuntimeResourcesAsync();
            }
            catch (OperationCanceledException) when (LifetimeToken.IsCancellationRequested)
            {
                AppLog.Info($"이미지 빌드가 애플리케이션 종료로 취소되었습니다: {targetImageName}");
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
                    try
                    {
                        Directory.Delete(buildContextPath, true);
                    }
                    catch (Exception cleanupException)
                    {
                        AppLog.Warning($"임시 빌드 폴더 정리 실패: {buildContextPath}", cleanupException);
                    }
                }
            }
        }

        private static async Task<PreparedDockerBuildContext> PrepareDockerBuildContextAsync(
            string dockerfileContent,
            string uploadedFilePath,
            CancellationToken cancellationToken)
        {
            bool hasUploadedDockerfile =
                !string.IsNullOrWhiteSpace(uploadedFilePath) && File.Exists(uploadedFilePath);
            bool requiresLocalFiles = DockerfileRequiresLocalBuildContext(dockerfileContent);

            if (hasUploadedDockerfile && requiresLocalFiles)
            {
                string contextPath = Path.GetDirectoryName(uploadedFilePath)
                    ?? throw new InvalidOperationException("Dockerfile 경로의 상위 폴더를 확인할 수 없습니다.");
                return new PreparedDockerBuildContext(
                    contextPath,
                    uploadedFilePath,
                    IsTemporary: false,
                    UsesDockerfileDirectory: true);
            }

            string temporaryContextPath = Path.Combine(
                Path.GetTempPath(),
                "DockerDiagramBuild_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(temporaryContextPath);

            string temporaryDockerfilePath = Path.Combine(temporaryContextPath, "Dockerfile");
            try
            {
                await File.WriteAllTextAsync(
                    temporaryDockerfilePath,
                    dockerfileContent,
                    cancellationToken);
            }
            catch
            {
                Directory.Delete(temporaryContextPath, recursive: true);
                throw;
            }

            return new PreparedDockerBuildContext(
                temporaryContextPath,
                temporaryDockerfilePath,
                IsTemporary: true,
                UsesDockerfileDirectory: false);
        }

        private static bool DockerfileRequiresLocalBuildContext(string dockerfileContent) =>
            DockerfileLocalFileInstructionRegex().IsMatch(dockerfileContent ?? string.Empty) ||
            DockerfileLocalBindMountRegex().IsMatch(dockerfileContent ?? string.Empty);

        private readonly record struct PreparedDockerBuildContext(
            string ContextPath,
            string DockerfilePath,
            bool IsTemporary,
            bool UsesDockerfileDirectory);

        [System.Text.RegularExpressions.GeneratedRegex(@"^([a-zA-Z]:[\\/]|/|\.|~)")]
        private static partial System.Text.RegularExpressions.Regex MyRegex();

        [System.Text.RegularExpressions.GeneratedRegex(
            @"(?<!\S)--name(?:(?:\s*=\s*)|\s+)(?:""[^""]*""|'[^']*'|\S+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
        private static partial System.Text.RegularExpressions.Regex DockerNameOptionRegex();

        [System.Text.RegularExpressions.GeneratedRegex(
            @"\bdocker\s+run\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
        private static partial System.Text.RegularExpressions.Regex DockerRunCommandRegex();

        [System.Text.RegularExpressions.GeneratedRegex("\"[^\"]*\"|'[^']*'|\\S+")]
        private static partial System.Text.RegularExpressions.Regex DockerCliTokenRegex();

        [System.Text.RegularExpressions.GeneratedRegex(@"(?im)^[0-9a-f]{12,64}\s*$")]
        private static partial System.Text.RegularExpressions.Regex DockerContainerIdRegex();

        [System.Text.RegularExpressions.GeneratedRegex(@"[^a-zA-Z0-9_.-]")]
        private static partial System.Text.RegularExpressions.Regex InvalidDockerObjectNameCharacterRegex();

        [System.Text.RegularExpressions.GeneratedRegex(
            @"(?im)^\s*(?:COPY|ADD)\s+")]
        private static partial System.Text.RegularExpressions.Regex DockerfileLocalFileInstructionRegex();

        [System.Text.RegularExpressions.GeneratedRegex(
            @"(?im)^\s*RUN\s+.*--mount\s*=\s*[^\r\n]*type\s*=\s*bind")]
        private static partial System.Text.RegularExpressions.Regex DockerfileLocalBindMountRegex();
    }
}
