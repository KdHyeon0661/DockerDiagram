using DockerDiagram.Infrastructure;
using DockerDiagram.Contracts;
using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.ApplicationServices
{
    public readonly record struct DiagramSaveResult(bool Success, string? ErrorMessage)
    {
        public static DiagramSaveResult Succeeded() => new(true, null);
        public static DiagramSaveResult Failed(string errorMessage) => new(false, errorMessage);

        public string GetUserMessage(string path)
        {
            string detail = string.IsNullOrWhiteSpace(ErrorMessage)
                ? "알 수 없는 저장 오류가 발생했습니다."
                : ErrorMessage;

            return $"파일을 저장하지 못했습니다.\n경로: {path}\n원인: {detail}";
        }
    }

    /// <summary>
    /// 다이어그램(Sheet)의 상태를 파일(.vdm)로 저장하거나 반대로 불러오는(Load) 정적 서비스 클래스입니다.
    /// SSH 원격 접속 프로필과 같이 저장된 시트의 경우, 불러올 때 자동으로 터널링을 복구합니다.
    /// </summary>
    public static class FileService
    {
        public static string MakeSafeFileName(string value, string fallbackName)
        {
            foreach (var invalidChar in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalidChar, '_');
            }

            return string.IsNullOrWhiteSpace(value) ? fallbackName : value;
        }

        /// <summary>
        /// 사용자에게 다이얼로그를 띄워 파일 경로를 지정받은 뒤, 현재 화면의 다이어그램을 파일로 저장합니다.
        /// </summary>
        public static string? SaveDiagramAs(MainViewModel mainVm, IDialogService dialogService)
        {
            string defaultFileName = !string.IsNullOrEmpty(mainVm.CurrentFilePath)
                ? Path.GetFileName(mainVm.CurrentFilePath)
                : "MyDockerLayout";

            string? selectedFileName = dialogService.ShowSaveFileDialog(
                "Docker Diagram (*.vdm)|*.vdm",
                ".vdm",
                defaultFileName,
                "Save Diagram As");

            if (string.IsNullOrEmpty(selectedFileName))
                return null;

            DiagramSaveResult result = InternalSave(mainVm, selectedFileName);
            if (result.Success)
            {
                SaveLastFilePath(selectedFileName); // 다음 실행 시 자동 로드를 위해 경로 저장
                dialogService.ShowInfo($"저장되었습니다.\n{selectedFileName}", "완료");
                return selectedFileName;
            }

            dialogService.ShowError(result.GetUserMessage(selectedFileName), "저장 실패");
            return null;
        }

        /// <summary>
        /// 다이얼로그 없이 기존에 저장된 경로에 즉시 덮어쓰기 저장을 수행합니다. (빠른 저장)
        /// </summary>
        public static bool QuickSave(MainViewModel mainVm, string path) =>
            QuickSaveWithResult(mainVm, path).Success;

        public static DiagramSaveResult QuickSaveWithResult(MainViewModel mainVm, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return DiagramSaveResult.Failed("저장 경로가 비어 있습니다.");

            DiagramSaveResult result = InternalSave(mainVm, path);
            if (result.Success)
                SaveLastFilePath(path);

            return result;
        }

        /// <summary>
        /// 실제 데이터 매핑 및 JSON 직렬화를 수행하여 지정된 경로에 파일을 쓰는 내부(Internal) 메서드입니다.
        /// </summary>
        private static DiagramSaveResult InternalSave(MainViewModel mainVm, string filePath)
        {
            try
            {
                var fileData = new DiagramFile(); // 파일의 최상위 루트 껍데기 생성

                var allSheets = mainVm.AllSheets.ToList();
                if (mainVm.ActiveSheet != null)
                    fileData.ActiveSheetIndex = allSheets.IndexOf(mainVm.ActiveSheet);

                // 모든 시트(탭)를 순회하며 데이터 긁어모으기
                foreach (var sheetVm in allSheets)
                {
                    sheetVm.Profile.RuntimeKind = sheetVm.RuntimeKind;
                    var sheetData = new SheetData
                    {
                        Title = sheetVm.Title,
                        Profile = sheetVm.Profile, // 시트의 도커 연결 프로필(로컬/원격) 정보 저장
                        RuntimeKind = sheetVm.RuntimeKind,
                        MapWidth = sheetVm.MapWidth,
                        MapHeight = sheetVm.MapHeight,
                        Scale = sheetVm.Scale,
                        HasViewportCenter = sheetVm.HasViewportCenter,
                        ViewportCenterX = sheetVm.ViewportCenterX,
                        ViewportCenterY = sheetVm.ViewportCenterY,
                        ComposeRawYaml = sheetVm.ComposeRawYaml
                    };

                    // 시트 안의 모든 노드 정보 수집
                    foreach (var nodeVm in sheetVm.Nodes)
                    {
                        sheetData.Nodes.Add(new NodeData
                        {
                            Id = nodeVm.Id,
                            DockerId = nodeVm.ContainerId,
                            Name = nodeVm.Name,
                            ImageName = nodeVm.ImageName,
                            PortInfo = nodeVm.PortInfo,
                            Type = nodeVm.Type,
                            X = nodeVm.X,
                            Y = nodeVm.Y,
                            Width = nodeVm.Width,
                            Height = nodeVm.Height,
                            PortBindings = nodeVm.PortBindings ?? new List<string>(),
                            EnvironmentVariables = nodeVm.EnvironmentVariables ?? new List<string>(),
                            NetworkStaticIps = new Dictionary<string, string>(nodeVm.NetworkIpMap),
                            NetworkOptions = nodeVm.NetworkOptionsMap.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
                            DockerVolumeName = nodeVm.DockerVolumeName,
                            VolumeExternal = nodeVm.VolumeExternal,
                            VolumeLabels = new Dictionary<string, string>(nodeVm.VolumeLabels),
                            VolumeDriverOptions = new Dictionary<string, string>(nodeVm.VolumeDriverOptions),
                            RestartPolicy = nodeVm.RestartPolicy ?? "no",
                            ComposeProjectName = nodeVm.ComposeProjectName,
                            ComposeProjectIdentity = nodeVm.ComposeProjectIdentity,
                            ComposeServiceName = nodeVm.ComposeServiceName,
                            ComposeContainerNumber = nodeVm.ComposeContainerNumber,
                            ComposeLayoutInstanceId = nodeVm.ComposeLayoutInstanceId,
                            ComposePlacementWarning = nodeVm.ComposePlacementWarning,
                            ComposeRawServiceYaml = nodeVm.ComposeRawServiceYaml,
                            ComposeRawVolumeYaml = nodeVm.ComposeRawVolumeYaml,
                            IsSwarmService = nodeVm.IsSwarmService,
                            SwarmMode = nodeVm.SwarmMode,
                            SwarmDesiredReplicas = nodeVm.SwarmDesiredReplicas,
                            SwarmRunningReplicas = nodeVm.SwarmRunningReplicas,
                            IsKubernetesPod = nodeVm.IsKubernetesPod,
                            KubernetesKind = nodeVm.KubernetesKind,
                            KubernetesApiResource = nodeVm.KubernetesApiResource,
                            KubernetesApiVersion = nodeVm.KubernetesApiVersion,
                            KubernetesNamespace = nodeVm.KubernetesNamespace,
                            KubernetesNodeName = nodeVm.KubernetesNodeName,
                            KubernetesReady = nodeVm.KubernetesReady,
                            KubernetesRestarts = nodeVm.KubernetesRestarts,
                            KubernetesDesiredReplicas = nodeVm.KubernetesDesiredReplicas,
                            KubernetesReadyReplicas = nodeVm.KubernetesReadyReplicas,
                            KubernetesPodIp = nodeVm.KubernetesPodIp,
                            KubernetesPodDescribeText = nodeVm.KubernetesPodDescribeText,
                            KubernetesPodYamlText = nodeVm.KubernetesPodYamlText,
                            KubernetesPodJsonText = nodeVm.KubernetesPodJsonText
                        });
                    }

                    // 시트 안의 모든 연결선 정보 수집
                    foreach (var connVm in sheetVm.Connectors)
                    {
                        sheetData.Connections.Add(new ConnectionData
                        {
                            SourceNodeId = connVm.Source.Id,
                            TargetNodeId = connVm.Target.Id,
                            SourceDir = connVm.SourceDir,
                            TargetDir = connVm.TargetDir,
                            RelationType = connVm.RelationType,
                            IsBidirectional = connVm.IsBidirectional,
                            SourceDataLabel = connVm.SourceDataLabel,
                            TargetDataLabel = connVm.TargetDataLabel,
                            MountPath = connVm.MountPath,
                            IpAddress = connVm.IpAddress
                        });
                    }

                    // 시트 안의 모든 그룹 정보 수집
                    foreach (var group in sheetVm.Groups)
                    {
                        if (string.IsNullOrEmpty(group.Id)) group.Id = Guid.NewGuid().ToString();

                        var gData = new GroupData
                        {
                            Id = group.Id,
                            Title = group.Title,
                            X = group.X,
                            Y = group.Y,
                            Width = group.Width,
                            Height = group.Height,
                            Type = group.Type,
                            Driver = group.Driver,
                            Subnet = group.Subnet,
                            Gateway = group.Gateway,
                            IpRange = group.IpRange,
                            Internal = group.Internal,
                            Attachable = group.Attachable,
                            EnableIPv6 = group.EnableIPv6,
                            External = group.External,
                            ComposeNetworkName = group.ComposeNetworkName,
                            ComposeProjectIdentity = group.ComposeProjectIdentity,
                            ComposeLayoutInstanceId = group.ComposeLayoutInstanceId,
                            ComposeRawNetworkYaml = group.ComposeRawNetworkYaml,
                            Labels = new Dictionary<string, string>(group.Labels),
                            DriverOptions = new Dictionary<string, string>(group.DriverOptions),
                            AuxAddresses = new Dictionary<string, string>(group.AuxAddresses)
                        };
                        gData.ContainedNodeIds = group.ContainedNodes.Select(n => n.Id).ToList();
                        sheetData.Groups.Add(gData);
                    }

                    fileData.Sheets.Add(sheetData);
                }

                // 같은 폴더의 임시 파일에 먼저 쓴 뒤 JSON 검증을 통과한 경우에만 원본과 교체합니다.
                var options = new JsonSerializerOptions { WriteIndented = true };
                string jsonString = JsonSerializer.Serialize(fileData, options);
                DiagramSaveResult result = WriteValidatedFileAtomically(filePath, jsonString);
                if (!result.Success)
                    return result;

                mainVm.IsModified = false; // "수정됨(*)" 상태 해제
                return DiagramSaveResult.Succeeded();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DockerDiscovery] Save Error: {ex}");
                return DiagramSaveResult.Failed(ex.Message);
            }
        }

        internal static DiagramSaveResult WriteValidatedFileAtomically(string filePath, string json)
        {
            string? temporaryPath = null;

            try
            {
                string fullPath = Path.GetFullPath(filePath);
                string? directory = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                    return DiagramSaveResult.Failed("저장할 폴더가 존재하지 않습니다.");

                temporaryPath = Path.Combine(
                    directory,
                    $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                string writtenJson = File.ReadAllText(temporaryPath);
                DiagramFile? validatedData = JsonSerializer.Deserialize<DiagramFile>(writtenJson);
                if (validatedData?.Sheets == null)
                    throw new InvalidDataException("저장된 파일을 다시 검증하지 못했습니다.");

                if (File.Exists(fullPath))
                {
                    string backupPath = fullPath + ".bak";
                    File.Replace(temporaryPath, fullPath, backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, fullPath);
                }

                temporaryPath = null;
                return DiagramSaveResult.Succeeded();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DockerDiscovery] Atomic save failed: {ex}");
                return DiagramSaveResult.Failed(ex.Message);
            }
            finally
            {
                if (!string.IsNullOrEmpty(temporaryPath))
                {
                    try
                    {
                        if (File.Exists(temporaryPath))
                            File.Delete(temporaryPath);
                    }
                    catch (Exception cleanupException)
                    {
                        Debug.WriteLine($"[DockerDiscovery] Failed to remove temporary save file '{temporaryPath}'. {cleanupException}");
                    }
                }
            }
        }

        /// <summary>
        /// 사용자에게 다이얼로그를 띄워 저장된 다이어그램 파일(.vdm)을 선택받고, 화면에 복원합니다.
        /// </summary>
        public static async Task<string?> LoadDiagramWithDialogAsync(
            MainViewModel mainVm,
            IContainerService containerService,
            IVolumeService volumeService,
            INetworkService networkService,
            IDialogService dialogService)
        {
            string? selectedFileName = dialogService.ShowOpenFileDialog(
                "Docker Diagram (*.vdm)|*.vdm|JSON File (*.json)|*.json",
                "Load Diagram");

            if (!string.IsNullOrEmpty(selectedFileName))
            {
                bool success = await LoadDiagramFromPathAsync(mainVm, selectedFileName, containerService, volumeService, networkService, dialogService);

                if (success)
                {
                    SaveLastFilePath(selectedFileName);
                    return selectedFileName;
                }
            }
            return null; // 불러오기 취소 또는 실패 시
        }

        /// <summary>
        /// 지정된 경로의 다이어그램 파일(.vdm)을 읽어 C# 모델로 역직렬화하고, 
        /// 의존성 순서(노드 -> 그룹 -> 선)에 맞게 뷰모델을 생성하여 화면을 복원합니다.
        /// </summary>
        public static async Task<bool> LoadDiagramFromPathAsync(MainViewModel mainVm, string filePath, IContainerService containerService,
                                                                IVolumeService volumeService, INetworkService networkService, IDialogService dialogService)
        {
            ConnectionProfile? pendingProfile = null;
            IDockerService? pendingDockerService = null;
            bool pendingSshTunnel = false;

            try
            {
                if (!File.Exists(filePath)) return false;

                string json = await File.ReadAllTextAsync(filePath);
                var fileData = JsonSerializer.Deserialize<DiagramFile>(json);

                if (fileData == null) return false;

                mainVm.SheetManager.ClearAllWorkspaces();

                foreach (var sheetData in fileData.Sheets)
                {
                    // 저장된 연결 프로필이 없으면 로컬 연결로 처리합니다.
                    ConnectionProfile loadedProfile = sheetData.Profile ?? new ConnectionProfile { Name = "Local PC", Type = EndpointType.Local };
                    loadedProfile.RuntimeKind = sheetData.RuntimeKind;
                    IDockerService? sharedDockerService = mainVm.SheetManager.FindDockerServiceForConnection(loadedProfile);
                    IDockerService targetDockerService = sharedDockerService ?? (IDockerService)containerService;

                    // 같은 접속 대상의 다른 시트나 런타임 워크스페이스가 이미 복원됐다면
                    // 그 워크스페이스의 Docker 서비스와 SSH 터널을 그대로 공유합니다.
                    if (sharedDockerService == null &&
                        loadedProfile.Type == EndpointType.SshRemote &&
                        !string.IsNullOrEmpty(loadedProfile.HostIp))
                    {
                        try
                        {
                            pendingProfile = loadedProfile;
                            int localPort = await SshTunnelManager.GetOrStartTunnelAsync(
                                loadedProfile.HostIp,
                                loadedProfile.SshPort,
                                loadedProfile.SshUsername ?? "root",
                                loadedProfile.SshKeyFilePath ?? "",
                                loadedProfile.RemoteDockerSocketPath,
                                dialogService);

                            pendingSshTunnel = true;
                            loadedProfile.LocalTunnelPort = localPort;
                            pendingDockerService = mainVm.DockerServiceFactory.Create(loadedProfile);
                            targetDockerService = pendingDockerService;
                        }
                        catch (Exception ex)
                        {
                            CleanupPendingConnection(mainVm.DockerServiceFactory, pendingProfile, pendingDockerService, pendingSshTunnel);
                            pendingProfile = null;
                            pendingDockerService = null;
                            pendingSshTunnel = false;
                            dialogService.ShowMessage($"'{loadedProfile.Name}' 시트의 SSH 터널 복구에 실패했습니다.\n임시로 로컬 모드로 전환됩니다.\n({ex.Message})");
                            loadedProfile.Type = EndpointType.Local;
                            targetDockerService = (IDockerService)containerService;
                        }
                    }
                    else if (sharedDockerService == null && loadedProfile.Type == EndpointType.SshRemote)
                    {
                        dialogService.ShowMessage($"'{loadedProfile.Name}' 시트의 SSH 호스트 정보가 없어 로컬 모드로 전환됩니다.");
                        loadedProfile.Type = EndpointType.Local;
                        targetDockerService = (IDockerService)containerService;
                    }
                    else if (sharedDockerService == null && loadedProfile.Type == EndpointType.DockerContext)
                    {
                        try
                        {
                            pendingProfile = loadedProfile;
                            pendingDockerService = mainVm.DockerServiceFactory.Create(loadedProfile);
                            targetDockerService = pendingDockerService;
                        }
                        catch (Exception ex)
                        {
                            CleanupPendingConnection(mainVm.DockerServiceFactory, pendingProfile, pendingDockerService, releaseSshTunnel: false);
                            pendingProfile = null;
                            pendingDockerService = null;
                            dialogService.ShowMessage($"'{loadedProfile.Name}' Docker context 복구에 실패했습니다.\n임시로 로컬 모드로 전환됩니다.\n({ex.Message})");
                            loadedProfile.Type = EndpointType.Local;
                            targetDockerService = (IDockerService)containerService;
                        }
                    }

                    // 복원된 프로필과 워크스페이스 단위 Docker 서비스로 시트 생성
                    var sheetVm = new SheetViewModel(sheetData.Title, loadedProfile, targetDockerService, dialogService, sheetData.RuntimeKind);
                    sheetVm.MapWidth = sheetData.MapWidth;
                    sheetVm.MapHeight = sheetData.MapHeight;
                    sheetVm.Scale = sheetData.Scale;
                    sheetVm.HasViewportCenter = sheetData.HasViewportCenter;
                    sheetVm.ViewportCenterX = sheetData.ViewportCenterX;
                    sheetVm.ViewportCenterY = sheetData.ViewportCenterY;
                    sheetVm.ComposeRawYaml = sheetData.ComposeRawYaml ?? string.Empty;

                    var itemMap = new Dictionary<string, IConnectableItem>(); // 노드와 선을 엮기 위한 임시 딕셔너리

                    var currentContainerSvc = (IContainerService)targetDockerService;
                    var currentVolumeSvc = (IVolumeService)targetDockerService;
                    var currentNetworkSvc = (INetworkService)targetDockerService;

                    // ==========================================
                    // 의존성 복원 단계 1: 노드 (가장 먼저 생성)
                    // ==========================================
                    foreach (var nodeData in sheetData.Nodes)
                    {
                        var nodeVm = new NodeViewModel(currentContainerSvc, currentVolumeSvc, dialogService)
                        {
                            Id = nodeData.Id,
                            ContainerId = nodeData.DockerId,
                            Name = nodeData.Name,
                            ImageName = nodeData.ImageName,
                            PortInfo = nodeData.PortInfo ?? string.Empty,
                            Type = nodeData.Type,
                            X = nodeData.X,
                            Y = nodeData.Y,
                            Width = nodeData.Width,
                            Height = nodeData.Height,
                            PortBindings = nodeData.PortBindings ?? new List<string>(),
                            EnvironmentVariables = nodeData.EnvironmentVariables ?? new List<string>(),
                            NetworkIpMap = nodeData.NetworkStaticIps ?? new Dictionary<string, string>(),
                            NetworkOptionsMap = nodeData.NetworkOptions?.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()) ?? new Dictionary<string, ContainerNetworkOptions>(),
                            DockerVolumeName = nodeData.DockerVolumeName ?? string.Empty,
                            VolumeExternal = nodeData.VolumeExternal,
                            VolumeLabels = nodeData.VolumeLabels ?? new Dictionary<string, string>(),
                            VolumeDriverOptions = nodeData.VolumeDriverOptions ?? new Dictionary<string, string>(),
                            RestartPolicy = nodeData.RestartPolicy ?? "no",
                            ComposeProjectName = nodeData.ComposeProjectName ?? string.Empty,
                            ComposeProjectIdentity = nodeData.ComposeProjectIdentity ?? string.Empty,
                            ComposeServiceName = nodeData.ComposeServiceName ?? string.Empty,
                            ComposeContainerNumber = nodeData.ComposeContainerNumber,
                            ComposeLayoutInstanceId = nodeData.ComposeLayoutInstanceId ?? string.Empty,
                            ComposePlacementWarning = nodeData.ComposePlacementWarning ?? string.Empty,
                            ComposeRawServiceYaml = nodeData.ComposeRawServiceYaml ?? string.Empty,
                            ComposeRawVolumeYaml = nodeData.ComposeRawVolumeYaml ?? string.Empty,
                            IsSwarmService = nodeData.IsSwarmService,
                            SwarmMode = nodeData.SwarmMode ?? string.Empty,
                            SwarmDesiredReplicas = nodeData.SwarmDesiredReplicas,
                            SwarmRunningReplicas = nodeData.SwarmRunningReplicas,
                            TargetSwarmReplicas = nodeData.SwarmDesiredReplicas,
                            IsKubernetesPod = nodeData.IsKubernetesPod,
                            KubernetesKind = nodeData.KubernetesKind ?? string.Empty,
                            KubernetesApiResource = nodeData.KubernetesApiResource ?? string.Empty,
                            KubernetesApiVersion = nodeData.KubernetesApiVersion ?? string.Empty,
                            KubernetesNamespace = nodeData.KubernetesNamespace ?? string.Empty,
                            KubernetesNodeName = nodeData.KubernetesNodeName ?? string.Empty,
                            KubernetesReady = nodeData.KubernetesReady ?? string.Empty,
                            KubernetesRestarts = nodeData.KubernetesRestarts,
                            KubernetesDesiredReplicas = nodeData.KubernetesDesiredReplicas,
                            KubernetesReadyReplicas = nodeData.KubernetesReadyReplicas,
                            TargetKubernetesReplicas = nodeData.KubernetesDesiredReplicas,
                            KubernetesPodIp = nodeData.KubernetesPodIp ?? string.Empty,
                            KubernetesPodDescribeText = nodeData.KubernetesPodDescribeText ?? string.Empty,
                            KubernetesPodYamlText = nodeData.KubernetesPodYamlText ?? string.Empty,
                            KubernetesPodJsonText = nodeData.KubernetesPodJsonText ?? string.Empty,
                            StatusColor = nodeData.IsSwarmService || nodeData.IsKubernetesPod ? "#28a745" : "#808080" // 초기 색상은 회색(Unkown)으로 고정, 이후 실시간 갱신됨
                        };

                        sheetVm.Nodes.Add(nodeVm);
                        itemMap[nodeVm.Id] = nodeVm; // 나중에 선을 연결하기 위해 ID로 기억
                    }

                    // ==========================================
                    // 의존성 복원 단계 2: 그룹 (노드를 품어야 하므로 두 번째)
                    // ==========================================
                    foreach (var groupData in sheetData.Groups)
                    {
                        var groupVm = new GroupViewModel(
                            groupData.X,
                            groupData.Y,
                            groupData.Width,
                            groupData.Height,
                            currentNetworkSvc,
                            dialogService,
                            groupData.Title,
                            groupData.Type
                        )
                        {
                            Id = string.IsNullOrEmpty(groupData.Id) ? Guid.NewGuid().ToString() : groupData.Id,
                            ParentSheet = sheetVm,
                            Driver = string.IsNullOrWhiteSpace(groupData.Driver) ? "bridge" : groupData.Driver,
                            Subnet = groupData.Subnet,
                            Gateway = groupData.Gateway,
                            IpRange = groupData.IpRange,
                            Internal = groupData.Internal,
                            Attachable = groupData.Attachable,
                            EnableIPv6 = groupData.EnableIPv6,
                            External = groupData.External,
                            ComposeNetworkName = groupData.ComposeNetworkName ?? string.Empty,
                            ComposeProjectIdentity = groupData.ComposeProjectIdentity ?? string.Empty,
                            ComposeLayoutInstanceId = groupData.ComposeLayoutInstanceId ?? string.Empty,
                            ComposeRawNetworkYaml = groupData.ComposeRawNetworkYaml ?? string.Empty,
                            Labels = groupData.Labels ?? new Dictionary<string, string>(),
                            DriverOptions = groupData.DriverOptions ?? new Dictionary<string, string>(),
                            AuxAddresses = groupData.AuxAddresses ?? new Dictionary<string, string>()
                        };

                        // 그룹 안에 저장되어 있던 노드들을 다시 묶어줌
                        foreach (var nodeId in groupData.ContainedNodeIds)
                        {
                            if (itemMap.TryGetValue(nodeId, out var item) && item is NodeViewModel node)
                            {
                                // isRestoring 플래그를 통해 불필요한 도커 API(ConnectNetwork 등) 호출 방지
                                await groupVm.AddNodeAsync(node, isRestoring: true);
                            }
                        }
                        sheetVm.Groups.Add(groupVm);
                        itemMap[groupVm.Id] = groupVm; // 선 연결을 위해 그룹도 기억
                    }

                    // ==========================================
                    // 의존성 복원 단계 3: 연결선 (노드와 그룹이 모두 있어야 하므로 마지막)
                    // ==========================================
                    foreach (var connData in sheetData.Connections)
                    {
                        if (itemMap.TryGetValue(connData.SourceNodeId, out var source) &&
                            itemMap.TryGetValue(connData.TargetNodeId, out var target))
                        {
                            var connVm = new ConnectorViewModel(source, target, connData.SourceDir, connData.TargetDir, dialogService)
                            {
                                RelationType = connData.RelationType,
                                IsBidirectional = connData.IsBidirectional,
                                SourceDataLabel = connData.SourceDataLabel,
                                TargetDataLabel = connData.TargetDataLabel,
                                MountPath = connData.MountPath,
                                IpAddress = connData.IpAddress
                            };

                            sheetVm.Connectors.Add(connVm);
                        }
                    }

                    mainVm.SheetManager.AddExistingSheet(sheetVm, activate: false);

                    if (ReferenceEquals(pendingDockerService, targetDockerService))
                    {
                        pendingProfile = null;
                        pendingDockerService = null;
                        pendingSshTunnel = false;
                    }
                }

                // 저장할 때 보고 있던 탭(ActiveSheet)으로 다시 포커스 이동
                var restoredSheets = mainVm.AllSheets.ToList();
                if (restoredSheets.Count > 0 && fileData.ActiveSheetIndex < restoredSheets.Count)
                {
                    mainVm.ActiveSheet = restoredSheets[fileData.ActiveSheetIndex];
                }
                else if (restoredSheets.Count == 0)
                {
                    mainVm.SheetManager.AddSheet();
                }

                return true;
            }
            catch (Exception ex)
            {
                CleanupPendingConnection(mainVm.DockerServiceFactory, pendingProfile, pendingDockerService, pendingSshTunnel);
                dialogService.ShowMessage($"파일 불러오기 실패 ({filePath}):\n{ex.Message}");
                return false;
            }
        }

        private static void CleanupPendingConnection(
            IDockerServiceFactory dockerServiceFactory,
            ConnectionProfile? profile,
            IDockerService? dockerService,
            bool releaseSshTunnel)
        {
            if (dockerService != null && !dockerServiceFactory.Release(dockerService))
            {
                dockerService.Dispose();
            }

            if (!releaseSshTunnel ||
                profile?.Type != EndpointType.SshRemote ||
                string.IsNullOrWhiteSpace(profile.HostIp)) return;

            SshTunnelManager.ReleaseTunnel(
                profile.HostIp,
                profile.SshPort,
                profile.SshUsername ?? "root",
                profile.RemoteDockerSocketPath);
        }

        /// <summary>
        /// 프로그램이 다시 켜졌을 때 마지막으로 작업하던 파일을 자동으로 불러오기 위해 경로를 레지스트리/설정에 저장합니다.
        /// </summary>
        private static void SaveLastFilePath(string path)
        {
            try
            {
                Properties.Settings.Default.LastFilePath = path;
                Properties.Settings.Default.Save();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DockerDiscovery] Failed to save LastFilePath='{path}'. {ex}");
            }
        }
    }
}
