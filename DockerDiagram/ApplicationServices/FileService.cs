using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Infrastructure;
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

    public readonly record struct DiagramRecoverySaveResult(
        DiagramSaveResult SaveResult,
        string? RecoveryPath);

    public sealed record DiagramRecoveryCandidate(
        string RecoveryPath,
        DateTime CapturedAtUtc,
        string OriginalFilePath);

    /// <summary>
    /// 다이어그램(Sheet)의 상태를 파일(.vdm)로 저장하거나 반대로 불러오는(Load) 정적 서비스 클래스입니다.
    /// SSH 원격 접속 프로필과 같이 저장된 시트의 경우, 불러올 때 자동으로 터널링을 복구합니다.
    /// </summary>
    public static class FileService
    {
        private static readonly ConcurrentDictionary<string, object> SaveLocks =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly string RecoveryDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DockerDiagram",
            "Recovery");
        private static readonly string UntitledRecoveryId = Guid.NewGuid().ToString("N");
        private static readonly JsonSerializerOptions IndentedJsonOptions = new()
        {
            WriteIndented = true
        };

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

        public static async Task<DiagramSaveResult> QuickSaveWithResultAsync(
            MainViewModel mainVm,
            string path,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path))
                return DiagramSaveResult.Failed("저장 경로가 비어 있습니다.");

            try
            {
                long savedVersion = mainVm.SheetManager.ModificationVersion;
                DiagramFile snapshot = CreateSaveSnapshot(mainVm);
                DiagramSaveResult result = await Task.Run(
                    () => PersistSnapshot(path, snapshot),
                    cancellationToken);

                if (result.Success)
                {
                    mainVm.SheetManager.MarkSavedIfCurrent(savedVersion);
                    SaveLastFilePath(path);
                }

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return DiagramSaveResult.Failed("저장이 취소되었습니다.");
            }
            catch (Exception ex)
            {
                AppLog.Error("비동기 다이어그램 저장 실패", ex);
                return DiagramSaveResult.Failed(ex.Message);
            }
        }

        public static async Task<DiagramRecoverySaveResult> SaveRecoverySnapshotAsync(
            MainViewModel mainVm,
            string? existingRecoveryPath,
            CancellationToken cancellationToken = default)
        {
            try
            {
                DiagramFile snapshot = CreateSaveSnapshot(mainVm);
                string originalFilePath = string.IsNullOrWhiteSpace(mainVm.CurrentFilePath)
                    ? string.Empty
                    : Path.GetFullPath(mainVm.CurrentFilePath);
                snapshot.Recovery = new DiagramRecoveryMetadata
                {
                    CapturedAtUtc = DateTime.UtcNow,
                    OriginalFilePath = originalFilePath
                };

                string recoveryPath = IsManagedRecoveryPath(existingRecoveryPath)
                    ? Path.GetFullPath(existingRecoveryPath!)
                    : CreateRecoveryPath(originalFilePath);

                Directory.CreateDirectory(RecoveryDirectory);
                DiagramSaveResult result = await Task.Run(
                    () =>
                    {
                        DiagramSaveResult saveResult = PersistSnapshot(recoveryPath, snapshot);
                        if (cancellationToken.IsCancellationRequested && saveResult.Success)
                        {
                            DeleteRecoveryFile(recoveryPath);
                            return DiagramSaveResult.Failed("복구 저장이 취소되었습니다.");
                        }

                        return saveResult;
                    },
                    cancellationToken);
                return new DiagramRecoverySaveResult(
                    result,
                    result.Success ? recoveryPath : existingRecoveryPath);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new DiagramRecoverySaveResult(
                    DiagramSaveResult.Failed("복구 저장이 취소되었습니다."),
                    existingRecoveryPath);
            }
            catch (Exception ex)
            {
                AppLog.Error("프로젝트 복구 스냅샷 저장 실패", ex);
                return new DiagramRecoverySaveResult(
                    DiagramSaveResult.Failed(ex.Message),
                    existingRecoveryPath);
            }
        }

        public static IReadOnlyList<DiagramRecoveryCandidate> GetRecoveryCandidates()
        {
            string[] recoveryFiles;
            try
            {
                if (!Directory.Exists(RecoveryDirectory))
                    return [];

                recoveryFiles = Directory.GetFiles(
                    RecoveryDirectory,
                    "*.recovery.vdm",
                    SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                AppLog.Warning("프로젝트 복구 폴더를 확인하지 못했습니다.", ex);
                return [];
            }

            var candidates = new List<DiagramRecoveryCandidate>();
            foreach (string recoveryPath in recoveryFiles)
            {
                try
                {
                    DiagramFile fileData = DeserializeAndValidateDiagram(
                        File.ReadAllText(recoveryPath));
                    DiagramRecoveryMetadata? metadata = fileData.Recovery;
                    if (metadata == null)
                        continue;

                    string originalPath = metadata.OriginalFilePath ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(originalPath) &&
                        File.Exists(originalPath) &&
                        File.GetLastWriteTimeUtc(originalPath) >= metadata.CapturedAtUtc)
                    {
                        DeleteRecoveryFile(recoveryPath);
                        continue;
                    }

                    candidates.Add(new DiagramRecoveryCandidate(
                        recoveryPath,
                        metadata.CapturedAtUtc,
                        originalPath));
                }
                catch (Exception ex)
                {
                    AppLog.Warning($"복구 파일 검사 실패: {recoveryPath}", ex);
                }
            }

            return candidates
                .OrderByDescending(candidate => candidate.CapturedAtUtc)
                .ToList();
        }

        public static void DeleteRecoveryFile(string? recoveryPath)
        {
            if (!IsManagedRecoveryPath(recoveryPath))
                return;

            string fullPath = Path.GetFullPath(recoveryPath!);
            object saveLock = SaveLocks.GetOrAdd(fullPath, _ => new object());
            lock (saveLock)
            {
                try
                {
                    if (File.Exists(fullPath))
                        File.Delete(fullPath);

                    string backupPath = fullPath + ".bak";
                    if (File.Exists(backupPath))
                        File.Delete(backupPath);
                }
                catch (Exception ex)
                {
                    AppLog.Warning($"복구 파일 삭제 실패: {fullPath}", ex);
                }
            }
        }

        public static void ClearLastFilePath()
        {
            try
            {
                Properties.Settings.Default.LastFilePath = string.Empty;
                Properties.Settings.Default.Save();
            }
            catch (Exception ex)
            {
                AppLog.Warning("최근 프로젝트 경로 초기화 실패", ex);
            }
        }

        private static string CreateRecoveryPath(string? originalFilePath)
        {
            string fileName;
            if (string.IsNullOrWhiteSpace(originalFilePath))
            {
                fileName = $"untitled-{UntitledRecoveryId}.recovery.vdm";
            }
            else
            {
                string normalizedPath = Path.GetFullPath(originalFilePath).ToUpperInvariant();
                byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
                fileName = $"file-{Convert.ToHexString(hash)[..24]}.recovery.vdm";
            }

            return Path.Combine(RecoveryDirectory, fileName);
        }

        private static bool IsManagedRecoveryPath(string? recoveryPath)
        {
            if (string.IsNullOrWhiteSpace(recoveryPath))
                return false;

            string fullPath = Path.GetFullPath(recoveryPath);
            string fullRecoveryDirectory = Path.GetFullPath(RecoveryDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullRecoveryDirectory, StringComparison.OrdinalIgnoreCase) &&
                   fullPath.EndsWith(".recovery.vdm", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 실제 데이터 매핑 및 JSON 직렬화를 수행하여 지정된 경로에 파일을 쓰는 내부(Internal) 메서드입니다.
        /// </summary>
        private static DiagramSaveResult InternalSave(MainViewModel mainVm, string filePath)
        {
            try
            {
                long savedVersion = mainVm.SheetManager.ModificationVersion;
                DiagramFile fileData = CreateSaveSnapshot(mainVm);
                DiagramSaveResult result = PersistSnapshot(filePath, fileData);
                if (result.Success)
                    mainVm.SheetManager.MarkSavedIfCurrent(savedVersion);
                return result;
            }
            catch (Exception ex)
            {
                AppLog.Error("다이어그램 저장 실패", ex);
                return DiagramSaveResult.Failed(ex.Message);
            }
        }

        private static DiagramFile CreateSaveSnapshot(MainViewModel mainVm)
        {
                var fileData = new DiagramFile(); // 파일의 최상위 루트 껍데기 생성

                var allSheets = mainVm.AllSheets.ToList();
                if (mainVm.ActiveSheet != null)
                    fileData.ActiveSheetIndex = allSheets.IndexOf(mainVm.ActiveSheet);

                // 모든 시트(탭)를 순회하며 데이터 긁어모으기
                foreach (var sheetVm in allSheets)
                {
                    var sheetData = new SheetData
                    {
                        Title = sheetVm.Title,
                        Profile = CloneConnectionProfile(sheetVm.Profile, sheetVm.RuntimeKind),
                        RuntimeKind = sheetVm.RuntimeKind,
                        MapWidth = sheetVm.MapWidth,
                        MapHeight = sheetVm.MapHeight,
                        Scale = sheetVm.Scale,
                        HasViewportCenter = sheetVm.HasViewportCenter,
                        ViewportCenterX = sheetVm.ViewportCenterX,
                        ViewportCenterY = sheetVm.ViewportCenterY,
                        ComposeRawYaml = UserDataProtection.Protect(sheetVm.ComposeRawYaml)
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
                            Driver = nodeVm.Driver,
                            HostPath = nodeVm.HostPath,
                            PortInfo = nodeVm.PortInfo,
                            Type = nodeVm.Type,
                            RuntimeKind = nodeVm.RuntimeKind,
                            ResourceKind = nodeVm.ResourceKind,
                            BindingState = nodeVm.BindingState,
                            X = nodeVm.X,
                            Y = nodeVm.Y,
                            Width = nodeVm.Width,
                            Height = nodeVm.Height,
                            PortBindings = nodeVm.PortBindings?.ToList() ?? [],
                            EnvironmentVariables = UserDataProtection.ProtectAll(nodeVm.EnvironmentVariables),
                            MountSpecifications = nodeVm.MountSpecifications.Select(mount => mount.Clone()).ToList(),
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
                            ComposeRawServiceYaml = UserDataProtection.Protect(nodeVm.ComposeRawServiceYaml),
                            ComposeRawVolumeYaml = UserDataProtection.Protect(nodeVm.ComposeRawVolumeYaml),
                            IsSwarmService = nodeVm.IsSwarmService,
                            SwarmMode = nodeVm.SwarmMode,
                            SwarmDesiredReplicas = nodeVm.SwarmDesiredReplicas,
                            SwarmRunningReplicas = nodeVm.SwarmRunningReplicas,
                            SwarmDiagramManagesPublishedPorts = nodeVm.SwarmDiagramManagesPublishedPorts,
                            SwarmDiagramManagesMounts = nodeVm.SwarmDiagramManagesMounts,
                            SwarmDiagramManagesNetworks = nodeVm.SwarmDiagramManagesNetworks,
                            IsSwarmDiagramDirty = nodeVm.IsSwarmDiagramDirty,
                            IsAutoGeneratedSwarmTopologyNode = nodeVm.IsAutoGeneratedSwarmTopologyNode,
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
                            KubernetesPodDescribeText = UserDataProtection.Protect(nodeVm.KubernetesPodDescribeText),
                            KubernetesPodYamlText = UserDataProtection.Protect(nodeVm.KubernetesPodYamlText),
                            KubernetesPodJsonText = UserDataProtection.Protect(nodeVm.KubernetesPodJsonText)
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
                            MountKind = connVm.MountKind,
                            MountReadOnly = connVm.MountReadOnly,
                            MountOptions = connVm.MountOptions,
                            IpAddress = connVm.IpAddress,
                            PublishedPort = connVm.PublishedPort,
                            TargetPort = connVm.TargetPort,
                            Protocol = connVm.Protocol,
                            PublishMode = connVm.PublishMode,
                            SwarmReferenceTarget = connVm.SwarmReferenceTarget,
                            SwarmReferenceUid = connVm.SwarmReferenceUid,
                            SwarmReferenceGid = connVm.SwarmReferenceGid,
                            SwarmReferenceMode = connVm.SwarmReferenceMode
                        });
                    }

                    // 시트 안의 모든 그룹 정보 수집
                    foreach (var group in sheetVm.Groups)
                    {
                        if (group.Type == GroupType.Network &&
                            DockerNetworkSafety.IsProtected(group.DockerNetworkName))
                        {
                            continue;
                        }

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
                            RuntimeKind = group.RuntimeKind,
                            ResourceKind = group.ResourceKind,
                            BindingState = group.BindingState,
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
                            ComposeRawNetworkYaml = UserDataProtection.Protect(group.ComposeRawNetworkYaml),
                            Labels = new Dictionary<string, string>(group.Labels),
                            DriverOptions = new Dictionary<string, string>(group.DriverOptions),
                            AuxAddresses = new Dictionary<string, string>(group.AuxAddresses)
                        };
                        gData.ContainedNodeIds = group.ContainedNodes.Select(n => n.Id).ToList();
                        sheetData.Groups.Add(gData);
                    }

                    fileData.Sheets.Add(sheetData);
                }

                return fileData;
        }

        private static DiagramSaveResult PersistSnapshot(string filePath, DiagramFile fileData)
        {
            string jsonString = JsonSerializer.Serialize(fileData, IndentedJsonOptions);
            string fullPath = Path.GetFullPath(filePath);
            object saveLock = SaveLocks.GetOrAdd(fullPath, _ => new object());
            lock (saveLock)
            {
                return WriteValidatedFileAtomically(fullPath, jsonString);
            }
        }

        private static ConnectionProfile CloneConnectionProfile(
            ConnectionProfile source,
            RuntimeKind runtimeKind) =>
            new()
            {
                ProfileId = source.ProfileId,
                Name = source.Name,
                Type = source.Type,
                RuntimeKind = runtimeKind,
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
                _ = DeserializeAndValidateDiagram(writtenJson);

                string backupPath = fullPath + ".bak";
                if (File.Exists(fullPath))
                {
                    File.Replace(temporaryPath, fullPath, backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, fullPath);
                }

                temporaryPath = null;
                try
                {
                    if (File.Exists(backupPath))
                        File.Delete(backupPath);
                }
                catch (Exception cleanupException)
                {
                    AppLog.Warning($"저장 백업 파일 정리 실패: {backupPath}", cleanupException);
                }

                return DiagramSaveResult.Succeeded();
            }
            catch (Exception ex)
            {
                AppLog.Error("원자적 다이어그램 파일 저장 실패", ex);
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
                        AppLog.Warning(
                            $"임시 저장 파일 정리 실패: {temporaryPath}",
                            cleanupException);
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
            var stagedSheets = new List<SheetViewModel>();
            var stagedConnections = new List<StagedConnection>();
            bool committed = false;

            try
            {
                if (!File.Exists(filePath)) return false;

                string json = await File.ReadAllTextAsync(filePath);
                DiagramFile fileData = DeserializeAndValidateDiagram(json);

                foreach (var sheetData in fileData.Sheets)
                {
                    // 저장된 연결 프로필이 없으면 로컬 연결로 처리합니다.
                    ConnectionProfile loadedProfile = sheetData.Profile ?? new ConnectionProfile { Name = "Local PC", Type = EndpointType.Local };
                    loadedProfile.RuntimeKind = sheetData.RuntimeKind;
                    StagedConnection? sharedConnection = stagedConnections.FirstOrDefault(connection =>
                        IsSameConnection(connection.Profile, loadedProfile));
                    IDockerService? sharedDockerService = sharedConnection?.DockerService;
                    IDockerService targetDockerService = sharedDockerService ?? mainVm.SheetManager.DefaultDockerService;

                    // 같은 접속 대상의 다른 시트나 런타임 워크스페이스가 이미 복원됐다면
                    // 그 워크스페이스의 Docker 서비스와 SSH 터널을 그대로 공유합니다.
                    if (sharedDockerService == null &&
                        loadedProfile.Type == EndpointType.SshRemote &&
                        !string.IsNullOrEmpty(loadedProfile.HostIp))
                    {
                        bool tunnelStarted = false;
                        IDockerService? createdService = null;
                        try
                        {
                            int localPort = await SshTunnelManager.GetOrStartTunnelAsync(
                                loadedProfile.HostIp,
                                loadedProfile.SshPort,
                                loadedProfile.SshUsername ?? "root",
                                loadedProfile.SshKeyFilePath ?? "",
                                loadedProfile.RemoteDockerSocketPath,
                                dialogService);

                            tunnelStarted = true;
                            loadedProfile.LocalTunnelPort = localPort;
                            createdService = mainVm.DockerServiceFactory.Create(loadedProfile);
                            targetDockerService = createdService;
                            stagedConnections.Add(new StagedConnection(loadedProfile, targetDockerService, HasSshTunnel: true));
                        }
                        catch (Exception ex)
                        {
                            CleanupPendingConnection(
                                mainVm.DockerServiceFactory,
                                loadedProfile,
                                createdService,
                                tunnelStarted);
                            throw new InvalidOperationException(
                                $"'{loadedProfile.Name}' 원격 연결을 복구하지 못했습니다. " +
                                "잘못된 로컬 Docker에 연결하지 않도록 프로젝트 불러오기를 중단합니다.\n" +
                                $"원인: {ex.GetBaseException().Message}",
                                ex);
                        }
                    }
                    else if (sharedDockerService == null && loadedProfile.Type == EndpointType.SshRemote)
                    {
                        throw new InvalidDataException(
                            $"'{loadedProfile.Name}' 원격 연결에 필요한 SSH 호스트 정보가 없습니다.");
                    }
                    else if (sharedDockerService == null && loadedProfile.Type == EndpointType.DockerContext)
                    {
                        IDockerService? createdService = null;
                        try
                        {
                            createdService = mainVm.DockerServiceFactory.Create(loadedProfile);
                            targetDockerService = createdService;
                            stagedConnections.Add(new StagedConnection(loadedProfile, targetDockerService, HasSshTunnel: false));
                        }
                        catch (Exception ex)
                        {
                            CleanupPendingConnection(
                                mainVm.DockerServiceFactory,
                                loadedProfile,
                                createdService,
                                releaseSshTunnel: false);
                            throw new InvalidOperationException(
                                $"'{loadedProfile.Name}' Docker context를 복구하지 못했습니다. " +
                                "잘못된 로컬 Docker에 연결하지 않도록 프로젝트 불러오기를 중단합니다.\n" +
                                $"원인: {ex.GetBaseException().Message}",
                                ex);
                        }
                    }

                    // 복원된 프로필과 워크스페이스 단위 Docker 서비스로 시트 생성
                    var sheetVm = new SheetViewModel(sheetData.Title, loadedProfile, targetDockerService, dialogService, sheetData.RuntimeKind);
                    stagedSheets.Add(sheetVm);
                    sheetVm.MapWidth = sheetData.MapWidth;
                    sheetVm.MapHeight = sheetData.MapHeight;
                    sheetVm.Scale = sheetData.Scale;
                    sheetVm.HasViewportCenter = sheetData.HasViewportCenter;
                    sheetVm.ViewportCenterX = sheetData.ViewportCenterX;
                    sheetVm.ViewportCenterY = sheetData.ViewportCenterY;
                    sheetVm.ComposeRawYaml = UserDataProtection.Unprotect(sheetData.ComposeRawYaml);

                    var itemMap = new Dictionary<string, IConnectableItem>(); // 노드와 선을 엮기 위한 임시 딕셔너리

                    var currentContainerSvc = (IContainerService)targetDockerService;
                    var currentVolumeSvc = (IVolumeService)targetDockerService;
                    var currentNetworkSvc = (INetworkService)targetDockerService;

                    // ==========================================
                    // 의존성 복원 단계 1: 노드 (가장 먼저 생성)
                    // ==========================================
                    foreach (var nodeData in sheetData.Nodes)
                    {
                        string legacyStorageSource = !string.IsNullOrWhiteSpace(nodeData.HostPath)
                            ? nodeData.HostPath
                            : !string.IsNullOrWhiteSpace(nodeData.DockerVolumeName)
                                ? nodeData.DockerVolumeName
                                : nodeData.Name;
                        bool migrateLegacyBind = nodeData.Type == NodeType.Volume &&
                                                 ContainerMountParser.LooksLikeBindSource(legacyStorageSource);
                        NodeType loadedNodeType = migrateLegacyBind ? NodeType.HostPath : nodeData.Type;
                        RuntimeResourceKind loadedResourceKind = migrateLegacyBind
                            ? RuntimeResourceKind.HostPathReference
                            : nodeData.ResourceKind;

                        var nodeVm = DiagramNodeFactory.CreateForRestore(
                            currentContainerSvc,
                            currentVolumeSvc,
                            dialogService,
                            loadedNodeType);
                        NodeViewModel node = nodeVm;
                            node.Id = nodeData.Id;
                            node.ContainerId = nodeData.DockerId;
                            node.Name = nodeData.Name;
                            node.ImageName = nodeData.ImageName;
                            node.Driver = loadedNodeType == NodeType.Volume
                                ? string.IsNullOrWhiteSpace(nodeData.Driver)
                                    ? nodeData.ImageName ?? string.Empty
                                    : nodeData.Driver
                                : nodeData.Driver ?? string.Empty;
                            node.HostPath = migrateLegacyBind ? legacyStorageSource : nodeData.HostPath ?? string.Empty;
                            node.PortInfo = nodeData.PortInfo ?? string.Empty;
                            node.RuntimeKind = loadedResourceKind == RuntimeResourceKind.Unspecified
                                ? sheetData.RuntimeKind
                                : nodeData.RuntimeKind;
                            node.ResourceKind = loadedResourceKind;
                            node.BindingState = nodeData.BindingState;
                            node.X = nodeData.X;
                            node.Y = nodeData.Y;
                            node.Width = nodeData.Width;
                            node.Height = nodeData.Height;
                            node.PortBindings = nodeData.PortBindings ?? [];
                            node.EnvironmentVariables = UserDataProtection.UnprotectAll(nodeData.EnvironmentVariables);
                            node.NetworkIpMap = nodeData.NetworkStaticIps ?? [];
                            node.NetworkOptionsMap = nodeData.NetworkOptions?.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()) ?? [];
                            node.DockerVolumeName = nodeData.DockerVolumeName ?? string.Empty;
                            node.VolumeExternal = nodeData.VolumeExternal;
                            node.VolumeLabels = nodeData.VolumeLabels ?? [];
                            node.VolumeDriverOptions = nodeData.VolumeDriverOptions ?? [];
                            node.RestartPolicy = nodeData.RestartPolicy ?? "no";
                            node.ComposeProjectName = nodeData.ComposeProjectName ?? string.Empty;
                            node.ComposeProjectIdentity = nodeData.ComposeProjectIdentity ?? string.Empty;
                            node.ComposeServiceName = nodeData.ComposeServiceName ?? string.Empty;
                            node.ComposeContainerNumber = nodeData.ComposeContainerNumber;
                            node.ComposeLayoutInstanceId = nodeData.ComposeLayoutInstanceId ?? string.Empty;
                            node.ComposePlacementWarning = nodeData.ComposePlacementWarning ?? string.Empty;
                            node.ComposeRawServiceYaml = UserDataProtection.Unprotect(nodeData.ComposeRawServiceYaml);
                            node.ComposeRawVolumeYaml = UserDataProtection.Unprotect(nodeData.ComposeRawVolumeYaml);
                            node.IsSwarmService = nodeData.IsSwarmService;
                            node.SwarmMode = nodeData.SwarmMode ?? string.Empty;
                            node.SwarmDesiredReplicas = nodeData.SwarmDesiredReplicas;
                            node.SwarmRunningReplicas = nodeData.SwarmRunningReplicas;
                            node.SwarmDiagramManagesPublishedPorts = nodeData.SwarmDiagramManagesPublishedPorts;
                            node.SwarmDiagramManagesMounts = nodeData.SwarmDiagramManagesMounts;
                            node.SwarmDiagramManagesNetworks = nodeData.SwarmDiagramManagesNetworks;
                            node.IsSwarmDiagramDirty = nodeData.IsSwarmDiagramDirty;
                            node.IsAutoGeneratedSwarmTopologyNode = nodeData.IsAutoGeneratedSwarmTopologyNode;
                            node.TargetSwarmReplicas = nodeData.SwarmDesiredReplicas;
                            node.IsKubernetesPod = nodeData.IsKubernetesPod;
                            node.KubernetesKind = nodeData.KubernetesKind ?? string.Empty;
                            node.KubernetesApiResource = nodeData.KubernetesApiResource ?? string.Empty;
                            node.KubernetesApiVersion = nodeData.KubernetesApiVersion ?? string.Empty;
                            node.KubernetesNamespace = nodeData.KubernetesNamespace ?? string.Empty;
                            node.KubernetesNodeName = nodeData.KubernetesNodeName ?? string.Empty;
                            node.KubernetesReady = nodeData.KubernetesReady ?? string.Empty;
                            node.KubernetesRestarts = nodeData.KubernetesRestarts;
                            node.KubernetesDesiredReplicas = nodeData.KubernetesDesiredReplicas;
                            node.KubernetesReadyReplicas = nodeData.KubernetesReadyReplicas;
                            node.TargetKubernetesReplicas = nodeData.KubernetesDesiredReplicas;
                            node.KubernetesPodIp = nodeData.KubernetesPodIp ?? string.Empty;
                            node.KubernetesPodDescribeText = UserDataProtection.Unprotect(nodeData.KubernetesPodDescribeText);
                            node.KubernetesPodYamlText = UserDataProtection.Unprotect(nodeData.KubernetesPodYamlText);
                            node.KubernetesPodJsonText = UserDataProtection.Unprotect(nodeData.KubernetesPodJsonText);
                            node.StatusColor = nodeData.IsSwarmService || nodeData.IsKubernetesPod
                                ? StatusColors.Running
                                : StatusColors.Unknown; // 초기 색상은 회색(Unknown)으로 고정, 이후 실시간 갱신됨

                        nodeVm.SetMountSpecifications(nodeData.MountSpecifications);

                        nodeVm.ParentSheet = sheetVm;
                        sheetVm.Nodes.Add(nodeVm);
                        itemMap[nodeVm.Id] = nodeVm; // 나중에 선을 연결하기 위해 ID로 기억
                    }

                    // ==========================================
                    // 의존성 복원 단계 2: 그룹 (노드를 품어야 하므로 두 번째)
                    // ==========================================
                    foreach (var groupData in sheetData.Groups)
                    {
                        string dockerNetworkName = string.IsNullOrWhiteSpace(groupData.ComposeNetworkName)
                            ? groupData.Title
                            : groupData.ComposeNetworkName;
                        if (groupData.Type == GroupType.Network &&
                            DockerNetworkSafety.IsProtected(dockerNetworkName))
                        {
                            continue;
                        }

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
                            RuntimeKind = groupData.ResourceKind == RuntimeResourceKind.Unspecified
                                ? sheetData.RuntimeKind
                                : groupData.RuntimeKind,
                            ResourceKind = groupData.ResourceKind,
                            BindingState = groupData.BindingState,
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
                            ComposeRawNetworkYaml = UserDataProtection.Unprotect(groupData.ComposeRawNetworkYaml),
                            Labels = groupData.Labels ?? [],
                            DriverOptions = groupData.DriverOptions ?? [],
                            AuxAddresses = groupData.AuxAddresses ?? []
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
                                MountKind = connData.MountKind,
                                MountReadOnly = connData.MountReadOnly,
                                MountOptions = connData.MountOptions ?? string.Empty,
                                IpAddress = connData.IpAddress,
                                PublishedPort = connData.PublishedPort ?? string.Empty,
                                TargetPort = connData.TargetPort ?? string.Empty,
                                Protocol = string.IsNullOrWhiteSpace(connData.Protocol) ? "tcp" : connData.Protocol,
                                PublishMode = string.IsNullOrWhiteSpace(connData.PublishMode) ? "ingress" : connData.PublishMode,
                                SwarmReferenceTarget = connData.SwarmReferenceTarget ?? string.Empty,
                                SwarmReferenceUid = string.IsNullOrWhiteSpace(connData.SwarmReferenceUid) ? "0" : connData.SwarmReferenceUid,
                                SwarmReferenceGid = string.IsNullOrWhiteSpace(connData.SwarmReferenceGid) ? "0" : connData.SwarmReferenceGid,
                                SwarmReferenceMode = string.IsNullOrWhiteSpace(connData.SwarmReferenceMode) ? "0444" : connData.SwarmReferenceMode
                            };

                            if (connVm.RelationType is RelationType.SwarmSecretReference or RelationType.SwarmConfigReference &&
                                string.IsNullOrWhiteSpace(connVm.SwarmReferenceTarget))
                            {
                                NodeViewModel? resourceNode = new[] { source, target }
                                    .OfType<NodeViewModel>()
                                    .FirstOrDefault(node => node.ResourceKind is RuntimeResourceKind.SwarmSecret or RuntimeResourceKind.SwarmConfig);
                                connVm.SwarmReferenceTarget = resourceNode?.Name ?? "resource";
                            }

                            sheetVm.Connectors.Add(connVm);
                        }
                    }

                    // 1.43 이하 파일은 bind mount도 Volume 노드/연결로 저장했습니다.
                    // 로드 시 명시적 HostPath/마운트 사양으로 승격해 이후 저장부터 새 형식을 사용합니다.
                    foreach (ConnectorViewModel connector in sheetVm.Connectors.Where(candidate =>
                                 candidate.RelationType == RelationType.VolumeMount))
                    {
                        NodeViewModel? storage = new[] { connector.Source, connector.Target }
                            .OfType<NodeViewModel>()
                            .FirstOrDefault(node => node.Type is NodeType.Volume or NodeType.HostPath);
                        if (storage?.Type == NodeType.HostPath)
                            connector.MountKind = MountKind.Bind;
                    }

                    foreach (NodeViewModel container in sheetVm.Nodes.Where(node =>
                                 node.Type == NodeType.Container && node.MountSpecifications.Count == 0))
                    {
                        var migratedMounts = new List<ContainerMountSpec>();
                        foreach (ConnectorViewModel connector in sheetVm.Connectors.Where(candidate =>
                                     candidate.RelationType == RelationType.VolumeMount &&
                                     (ReferenceEquals(candidate.Source, container) || ReferenceEquals(candidate.Target, container))))
                        {
                            NodeViewModel? storage = ReferenceEquals(connector.Source, container)
                                ? connector.Target as NodeViewModel
                                : connector.Source as NodeViewModel;
                            if (storage?.Type is not (NodeType.Volume or NodeType.HostPath) ||
                                string.IsNullOrWhiteSpace(connector.MountPath))
                            {
                                continue;
                            }

                            migratedMounts.Add(new ContainerMountSpec
                            {
                                Kind = storage.Type == NodeType.HostPath ? MountKind.Bind : MountKind.Volume,
                                Source = storage.Type == NodeType.HostPath ? storage.HostPath : storage.EffectiveVolumeName,
                                Target = connector.MountPath,
                                ReadOnly = connector.MountReadOnly
                            });
                        }
                        if (migratedMounts.Count > 0)
                            container.SetMountSpecifications(migratedMounts);
                    }

                    // 1.38 이하 파일에는 관리 플래그가 없으므로 기존 관계에서 안전하게 승격합니다.
                    foreach (NodeViewModel serviceNode in sheetVm.Nodes.Where(node =>
                                 node.ResourceKind == RuntimeResourceKind.SwarmService))
                    {
                        ConnectorViewModel[] serviceConnectors = sheetVm.Connectors
                            .Where(connector => ReferenceEquals(connector.Source, serviceNode) ||
                                                ReferenceEquals(connector.Target, serviceNode))
                            .ToArray();
                        serviceNode.SwarmDiagramManagesPublishedPorts |= serviceConnectors.Any(connector =>
                            connector.RelationType == RelationType.SwarmPublishedPort);
                        serviceNode.SwarmDiagramManagesMounts |= serviceConnectors.Any(connector =>
                            connector.RelationType == RelationType.VolumeMount);
                        serviceNode.SwarmDiagramManagesNetworks |= sheetVm.Groups.Any(group =>
                            group.ResourceKind == RuntimeResourceKind.SwarmOverlayNetwork &&
                            group.ContainedNodes.Contains(serviceNode));
                    }

                }

                // 모든 시트를 메모리에서 성공적으로 만든 뒤에만 현재 작업을 교체합니다.
                mainVm.SheetManager.ClearAllWorkspaces();
                foreach (SheetViewModel stagedSheet in stagedSheets)
                    mainVm.SheetManager.AddExistingSheet(stagedSheet, activate: false);

                // 저장할 때 보고 있던 탭(ActiveSheet)으로 다시 포커스 이동
                var restoredSheets = mainVm.AllSheets.ToList();
                if (restoredSheets.Count > 0)
                {
                    int activeIndex = fileData.ActiveSheetIndex >= 0 && fileData.ActiveSheetIndex < restoredSheets.Count
                        ? fileData.ActiveSheetIndex
                        : 0;
                    mainVm.ActiveSheet = restoredSheets[activeIndex];
                }
                else
                {
                    mainVm.SheetManager.AddSheet(markModified: false);
                }

                committed = true;

                return true;
            }
            catch (Exception ex)
            {
                if (!committed)
                {
                    foreach (SheetViewModel stagedSheet in stagedSheets)
                        stagedSheet.Dispose();

                    foreach (StagedConnection connection in stagedConnections
                                 .GroupBy(item => item.DockerService, ReferenceEqualityComparer.Instance)
                                 .Select(group => group.First()))
                    {
                        CleanupPendingConnection(
                            mainVm.DockerServiceFactory,
                            connection.Profile,
                            connection.DockerService,
                            connection.HasSshTunnel);
                    }
                }
                dialogService.ShowMessage($"파일 불러오기 실패 ({filePath}):\n{ex.Message}");
                return false;
            }
        }

        private static DiagramFile DeserializeAndValidateDiagram(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Docker Diagram 파일의 최상위 값은 JSON 객체여야 합니다.");

            if (!root.TryGetProperty(nameof(DiagramFile.Sheets), out JsonElement sheetsElement) ||
                sheetsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    "Docker Diagram 파일에 필수 Sheets 배열이 없습니다. 다른 종류의 JSON 파일일 수 있습니다.");
            }

            if (root.TryGetProperty(nameof(DiagramFile.Version), out JsonElement versionElement))
            {
                if (versionElement.ValueKind != JsonValueKind.String ||
                    !Version.TryParse(versionElement.GetString(), out Version? fileVersion))
                {
                    throw new InvalidDataException("Docker Diagram 파일 버전 형식이 올바르지 않습니다.");
                }

                Version supportedVersion = Version.Parse(DiagramFile.CurrentVersion);
                if (fileVersion > supportedVersion)
                {
                    throw new InvalidDataException(
                        $"이 파일은 더 새로운 Docker Diagram 버전({fileVersion})에서 만들어졌습니다. " +
                        $"현재 지원 버전은 {supportedVersion}까지입니다.");
                }
            }

            DiagramFile? fileData = JsonSerializer.Deserialize<DiagramFile>(json);
            if (fileData?.Sheets == null)
                throw new InvalidDataException("Docker Diagram 파일 내용을 읽을 수 없습니다.");

            return fileData;
        }

        private sealed record StagedConnection(
            ConnectionProfile Profile,
            IDockerService DockerService,
            bool HasSshTunnel);

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
                    string.Equals(
                        string.IsNullOrWhiteSpace(left.RemoteDockerSocketPath) ? SshTunnelManager.DefaultRemoteDockerSocketPath : left.RemoteDockerSocketPath.Trim(),
                        string.IsNullOrWhiteSpace(right.RemoteDockerSocketPath) ? SshTunnelManager.DefaultRemoteDockerSocketPath : right.RemoteDockerSocketPath.Trim(),
                        StringComparison.Ordinal),
                EndpointType.DockerContext =>
                    string.Equals(left.DockerEndpoint, right.DockerEndpoint, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(left.DockerTlsCaPath, right.DockerTlsCaPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(left.DockerTlsCertPath, right.DockerTlsCertPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(left.DockerTlsKeyPath, right.DockerTlsKeyPath, StringComparison.OrdinalIgnoreCase) &&
                    left.DockerTlsEnabled == right.DockerTlsEnabled &&
                    left.DockerTlsSkipVerify == right.DockerTlsSkipVerify,
                _ => false
            };
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
                AppLog.Warning($"최근 파일 경로 저장 실패: {path}", ex);
            }
        }
    }
}
