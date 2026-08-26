using DockerDiagram.Contracts;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerDiagram.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DockerDiagram.Infrastructure
{
    /// <summary>
    /// 화면(ViewModel)을 대신하여 실제 도커 엔진(백엔드)과 직접 통신을 수행하는 핵심 서비스 클래스입니다.
    /// 로컬(Named Pipe/Socket) 접속뿐만 아니라 SSH 터널링을 통한 원격 서버 접속까지 모두 처리합니다.
    /// </summary>
    public partial class DockerApiService : IDockerService
    {
        private readonly DockerClient _client;
        private static readonly TimeSpan SystemDiskUsageCacheTtl = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan SystemInfoCacheTtl = TimeSpan.FromMinutes(5);
        private SystemDiskUsage? _systemDiskUsageCache;
        private DateTimeOffset _systemDiskUsageCacheAt = DateTimeOffset.MinValue;
        private readonly SemaphoreSlim _systemDiskUsageCacheLock = new(1, 1);
        private SystemInfoResponse? _systemInfoCache;
        private DateTimeOffset _systemInfoCacheAt = DateTimeOffset.MinValue;
        private readonly SemaphoreSlim _systemInfoCacheLock = new(1, 1);
        private bool _disposedValue; // 중복 해제 방지 플래그

        public ConnectionProfile CurrentProfile { get; private set; }

        /// <summary>
        /// 연결 프로필에 따라 로컬 또는 원격 Docker 클라이언트를 초기화합니다.
        /// </summary>
        public DockerApiService(ConnectionProfile profile)
        {
            CurrentProfile = profile;
            Uri dockerUri;

            // 1. 만약 로컬 PC 접속이라면 (기존 방식)
            if (profile.Type == EndpointType.Local)
            {
                dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? new Uri("npipe://./pipe/docker_engine")
                    : new Uri("unix:///var/run/docker.sock");
            }
            // 2. 만약 원격 서버(SSH) 접속이라면 (새로운 방식)
            else if (profile.Type == EndpointType.SshRemote)
            {
                // 터널링 매니저가 미리 뚫어둔 내 PC의 특정 포트(예: 23750)로 접속!
                dockerUri = new Uri($"tcp://127.0.0.1:{profile.LocalTunnelPort}");
            }
            else if (profile.Type == EndpointType.DockerContext)
            {
                if (string.IsNullOrWhiteSpace(profile.DockerEndpoint))
                {
                    throw new NotSupportedException("Docker context endpoint가 비어 있습니다.");
                }

                dockerUri = new Uri(profile.DockerEndpoint);
            }
            else
            {
                throw new NotSupportedException($"지원하지 않는 엔드포인트 타입입니다: {profile.Type}");
            }

            var config = new DockerClientConfiguration(dockerUri);
            _client = config.CreateClient();
        }

        // ---------------------------------------------------------
        // 상세 기능 구현
        // ---------------------------------------------------------

        /// <summary>
        /// 특정 컨테이너의 상세 설정(네트워크, 볼륨 마운트, 환경변수 등)을 깊이 있게 조회합니다.
        /// </summary>
        public async Task<ContainerInspectResponse> InspectContainerAsync(string containerId)
        {
            return await _client.Containers.InspectContainerAsync(containerId);
        }

        /// <summary>
        /// 특정 볼륨의 상세 정보를 조회합니다.
        /// </summary>
        public async Task<VolumeResponse> InspectVolumeAsync(string name)
        {
            return await _client.Volumes.InspectAsync(name);
        }

        /// <summary>
        /// 특정 네트워크의 상세 정보를 조회합니다.
        /// </summary>
        public async Task<NetworkResponse> InspectNetworkAsync(string networkId)
        {
            return await _client.Networks.InspectNetworkAsync(networkId);
        }

        /// <summary>
        /// 특정 볼륨을 마운트하여 사용하고 있는 컨테이너들의 이름 목록을 찾아 반환합니다.
        /// </summary>
        public async Task<List<string>> GetContainersUsingVolumeAsync(string volumeName)
        {
            var usage = await GetVolumeUsageDetailsAsync(volumeName);
            return usage.Select(u => u.ContainerName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public async Task<List<VolumeUsageInfo>> GetVolumeUsageDetailsAsync(string volumeName)
        {
            var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters { All = true });

            var result = new List<VolumeUsageInfo>();

            foreach (var c in containers)
            {
                if (c.Mounts == null) continue;

                foreach (var mount in c.Mounts.Where(m => IsVolumeMountForName(m, volumeName)))
                {
                    result.Add(new VolumeUsageInfo
                    {
                        ContainerId = c.ID,
                        ContainerName = c.Names.FirstOrDefault()?.TrimStart('/') ?? c.ID,
                        Destination = mount.Destination,
                        ReadWrite = mount.RW,
                        Mode = mount.Mode ?? string.Empty
                    });
                }
            }
            return result;
        }

        private static bool IsVolumeMountForName(MountPoint mount, string volumeName)
        {
            if (!string.Equals(mount.Type, "volume", StringComparison.OrdinalIgnoreCase)) return false;

            if (!string.IsNullOrWhiteSpace(mount.Name))
            {
                return string.Equals(mount.Name, volumeName, StringComparison.OrdinalIgnoreCase);
            }

            if (string.IsNullOrWhiteSpace(mount.Source)) return false;

            var normalizedSource = mount.Source.Replace('\\', '/').TrimEnd('/');
            var lastSeparator = normalizedSource.LastIndexOf('/');
            var lastSegment = lastSeparator >= 0 ? normalizedSource[(lastSeparator + 1)..] : normalizedSource;

            return string.Equals(lastSegment, volumeName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 지정된 컨테이너를 시작(Start)합니다.
        /// </summary>
        public async Task StartContainerAsync(string id)
            => await _client.Containers.StartContainerAsync(id, new ContainerStartParameters());

        /// <summary>
        /// 지정된 컨테이너를 안전하게 정지(Stop)시킵니다.
        /// </summary>
        public async Task StopContainerAsync(string id)
            => await _client.Containers.StopContainerAsync(id, new ContainerStopParameters { WaitBeforeKillSeconds = 5 });

        /// <summary>
        /// 지정된 컨테이너에 signal을 보내 즉시 종료합니다.
        /// </summary>
        public async Task KillContainerAsync(string id, string signal = "SIGKILL")
            => await _client.Containers.KillContainerAsync(id, new ContainerKillParameters { Signal = signal });

        /// <summary>
        /// 지정된 컨테이너의 Docker 이름을 변경합니다.
        /// </summary>
        public async Task RenameContainerAsync(string id, string newName)
            => await _client.Containers.RenameContainerAsync(id, new ContainerRenameParameters { NewName = newName }, CancellationToken.None);

        /// <summary>
        /// 지정된 컨테이너의 실행을 일시 정지(Pause)합니다.
        /// </summary>
        public async Task PauseContainerAsync(string id)
            => await _client.Containers.PauseContainerAsync(id);

        /// <summary>
        /// 일시 정지된 컨테이너를 다시 재개(Unpause)합니다.
        /// </summary>
        public async Task UnpauseContainerAsync(string id)
            => await _client.Containers.UnpauseContainerAsync(id);

        /// <summary>
        /// 지정된 컨테이너를 재시작(Restart)합니다.
        /// </summary>
        public async Task RestartContainerAsync(string id)
            => await _client.Containers.RestartContainerAsync(id, new ContainerRestartParameters());

        /// <summary>
        /// 지정된 컨테이너를 영구적으로 강제 삭제(Remove)합니다. 연결된 볼륨은 보호됩니다.
        /// </summary>
        public async Task RemoveContainerAsync(string id)
        {
            await _client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true, RemoveVolumes = false });
        }

        /// <summary>
        /// 컨테이너의 현재 파일시스템 상태를 새 이미지로 커밋합니다.
        /// </summary>
        public async Task<string> CommitContainerAsync(string containerId, string repository, string tag, string comment, string author, bool pause)
        {
            var response = await _client.Images.CommitContainerChangesAsync(new CommitContainerChangesParameters
            {
                ContainerID = containerId,
                RepositoryName = repository,
                Tag = tag,
                Comment = comment,
                Author = author,
                Pause = pause
            }, CancellationToken.None);

            return response.ID;
        }

        /// <summary>
        /// 컨테이너 파일시스템을 tar 아카이브로 내보냅니다.
        /// </summary>
        public async Task ExportContainerAsync(string containerId, string tarFilePath)
        {
            await using var source = await _client.Containers.ExportContainerAsync(containerId, CancellationToken.None);
            await using var target = File.Create(tarFilePath);
            await source.CopyToAsync(target);
        }

        /// <summary>
        /// 지정된 이미지와 태그를 도커 허브(또는 레지스트리)에서 로컬로 다운로드(Pull)합니다.
        /// </summary>
        public async Task PullImageAsync(string image, string tag, string? username = null, string? password = null, string? serverAddress = null)
        {
            AuthConfig? authConfig = null;

            // 아이디와 비밀번호가 모두 전달된 경우에만 인증 객체 생성
            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
            {
                authConfig = new AuthConfig
                {
                    Username = username,
                    Password = password,
                    ServerAddress = string.IsNullOrWhiteSpace(serverAddress) ? "https://index.docker.io/v1/" : serverAddress
                };
            }

            // authConfig가 null이면 일반 Public Pull로, 값이 있으면 Private Pull로 동작합니다.
            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = image, Tag = tag },
                authConfig,
                new Progress<JSONMessage>());
        }

        /// <summary>
        /// 사용자가 뷰모델에 설정한 각종 옵션(포트, 환경변수, 볼륨, 명령어 등)을 바탕으로 새 컨테이너를 생성하고 즉시 실행합니다.
        /// </summary>
        public async Task<string> CreateAndStartContainerAsync(
    string name, string image, string tag, List<string> ports, List<string> envs, List<string> volumes,
    string restartPolicy, long memoryMb, double cpuCount,
    string command = "", bool tty = false, string networkName = "", Dictionary<string, string>? labels = null)
        {
            (image, tag) = DockerImageReferenceParser.Split(image, tag);
            string fullImageName = $"{image}:{tag}";

            string safeContainerName = System.Text.RegularExpressions.Regex.Replace(name, "[^a-zA-Z0-9_.-]", "-");

            var portBindings = new Dictionary<string, IList<PortBinding>>();
            var exposedPorts = new Dictionary<string, EmptyStruct>();

            if (ports != null)
            {
                foreach (var p in ports)
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;

                    var parts = p.Split(':');
                    string hostPort = "0";
                    string rawContainerPort = "";

                    if (parts.Length >= 2)
                    {
                        hostPort = parts[0];
                        rawContainerPort = parts[1];
                    }
                    else if (parts.Length == 1)
                    {
                        hostPort = "0";
                        rawContainerPort = parts[0];
                    }
                    else continue;

                    string containerPort = rawContainerPort.Contains('/') ? rawContainerPort : rawContainerPort + "/tcp";
                    exposedPorts[containerPort] = new EmptyStruct();
                    portBindings[containerPort] = new List<PortBinding> { new PortBinding { HostPort = hostPort } };
                }
            }

            long memoryBytes = memoryMb > 0 ? memoryMb * 1024 * 1024 : 0;
            long nanoCpus = cpuCount > 0 ? (long)(cpuCount * 1_000_000_000) : 0;

            string safeRestartPolicy = (restartPolicy ?? "no").ToLower().Replace("-", "");

            if (!Enum.TryParse(typeof(RestartPolicyKind), safeRestartPolicy, true, out var policyEnum))
            {
                policyEnum = RestartPolicyKind.No;
            }

            var parameters = new CreateContainerParameters
            {
                Image = fullImageName,
                Name = safeContainerName,
                Env = envs ?? new List<string>(),
                ExposedPorts = exposedPorts,
                HostConfig = new HostConfig
                {
                    PortBindings = portBindings,
                    Binds = volumes ?? new List<string>(),
                    Memory = memoryBytes,
                    NanoCPUs = nanoCpus,
                    RestartPolicy = new RestartPolicy
                    {
                        Name = (RestartPolicyKind)policyEnum
                    },
                    NetworkMode = string.IsNullOrWhiteSpace(networkName) ? null : networkName
                },
                // 대화형 컨테이너는 TTY와 표준 입력을 유지합니다.
                Tty = tty,
                OpenStdin = tty,
                Labels = labels is { Count: > 0 } ? labels : null
            };

            if (!string.IsNullOrWhiteSpace(command))
            {
                parameters.Cmd = ParseCommandArguments(command);
            }

            var response = await _client.Containers.CreateContainerAsync(parameters);
            try
            {
                await _client.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());
                return response.ID;
            }
            catch
            {
                try
                {
                    await _client.Containers.RemoveContainerAsync(
                        response.ID,
                        new ContainerRemoveParameters { Force = true, RemoveVolumes = false });
                }
                catch
                {
                }
                throw;
            }
        }

        private static IList<string> ParseCommandArguments(string command)
        {
            var arguments = new List<string>();
            var current = new System.Text.StringBuilder();
            char? quote = null;

            for (int i = 0; i < command.Length; i++)
            {
                char ch = command[i];

                if (quote.HasValue)
                {
                    if (ch == quote.Value)
                    {
                        quote = null;
                    }
                    else if (ch == '\\' && quote == '"' && i + 1 < command.Length && command[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        current.Append(ch);
                    }
                    continue;
                }

                if (ch is '"' or '\'')
                {
                    quote = ch;
                }
                else if (char.IsWhiteSpace(ch))
                {
                    if (current.Length > 0)
                    {
                        arguments.Add(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(ch);
                }
            }

            if (quote.HasValue)
                throw new ArgumentException("명령어의 따옴표가 닫히지 않았습니다.", nameof(command));

            if (current.Length > 0)
                arguments.Add(current.ToString());

            return arguments;
        }

        /// <summary>
        /// 특정 컨테이너 내부의 파일이나 디렉토리를 호스트 PC(내 컴퓨터)의 지정된 경로로 복사(추출)해옵니다.
        /// </summary>
        public async Task CopyFromContainerAsync(string containerId, string containerPath, string hostPath)
        {
            var tarResponse = await _client.Containers.GetArchiveFromContainerAsync(containerId, new GetArchiveFromContainerParameters
            {
                Path = containerPath
            }, false);

            TarFile.ExtractToDirectory(tarResponse.Stream, hostPath, overwriteFiles: true);
        }

        /// <summary>
        /// 호스트 PC(내 컴퓨터)의 파일이나 디렉토리를 임시 TAR 아카이브로 압축한 뒤, 특정 컨테이너 내부의 지정된 경로로 복사(삽입)합니다.
        /// </summary>
        public async Task CopyToContainerAsync(string containerId, string hostPath, string containerPath)
        {
            string tempTarFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tar");

            try
            {
                if (Directory.Exists(hostPath))
                {
                    TarFile.CreateFromDirectory(hostPath, tempTarFile, includeBaseDirectory: false);
                }
                else if (File.Exists(hostPath))
                {
                    await using var archive = File.Create(tempTarFile);
                    using var writer = new TarWriter(archive, leaveOpen: false);
                    writer.WriteEntry(hostPath, Path.GetFileName(hostPath));
                }
                else
                {
                    throw new FileNotFoundException("호스트 파일 또는 폴더를 찾을 수 없습니다.", hostPath);
                }

                using (var fs = File.OpenRead(tempTarFile))
                {
                    await _client.Containers.ExtractArchiveToContainerAsync(containerId, new ContainerPathStatParameters
                    {
                        Path = containerPath,
                        AllowOverwriteDirWithFile = true
                    }, fs);
                }
            }
            finally
            {
                if (File.Exists(tempTarFile))
                {
                    File.Delete(tempTarFile);
                }
            }
        }

        /// <summary>
        /// 지정된 이름과 드라이버(기본값 local)를 사용하여 도커 엔진에 새로운 물리적 볼륨을 생성합니다.
        /// </summary>
        public async Task CreateVolumeAsync(string name, string driver)
        {
            await CreateVolumeAsync(VolumeCreateOptions.Basic(name, driver));
        }

        public async Task CreateVolumeAsync(VolumeCreateOptions options)
        {
            await _client.Volumes.CreateAsync(new VolumesCreateParameters
            {
                Name = options.EffectiveDockerVolumeName,
                Driver = string.IsNullOrWhiteSpace(options.Driver) ? "local" : options.Driver,
                Labels = options.Labels.Count > 0 ? options.Labels : null,
                DriverOpts = options.DriverOptions.Count > 0 ? options.DriverOptions : null
            });
        }

        /// <summary>
        /// 지정된 이름과 드라이버(기본값 bridge)를 사용하여 도커 엔진에 새로운 가상 네트워크 망을 생성하고 고유 ID를 반환합니다.
        /// </summary>
        public async Task<string> CreateNetworkAsync(string name, string driver)
        {
            return await CreateNetworkAsync(NetworkCreateOptions.Basic(name, driver));
        }

        public async Task<string> CreateNetworkAsync(NetworkCreateOptions options)
        {
            var response = await _client.Networks.CreateNetworkAsync(new NetworksCreateParameters
            {
                Name = options.Name,
                Driver = options.Driver,
                CheckDuplicate = true,
                Internal = options.Internal,
                Attachable = options.Attachable,
                EnableIPv6 = options.EnableIPv6,
                Labels = options.Labels.Count > 0 ? options.Labels : null,
                Options = options.DriverOptions.Count > 0 ? options.DriverOptions : null,
                IPAM = options.HasIpam
                    ? new IPAM
                    {
                        Config = new List<IPAMConfig>
                        {
                            new IPAMConfig
                            {
                                Subnet = string.IsNullOrWhiteSpace(options.Subnet) ? null : options.Subnet,
                        Gateway = string.IsNullOrWhiteSpace(options.Gateway) ? null : options.Gateway,
                        IPRange = string.IsNullOrWhiteSpace(options.IpRange) ? null : options.IpRange,
                        AuxAddress = options.AuxAddresses.Count > 0 ? options.AuxAddresses : null
                    }
                }
            }
                    : null
            });
            return response.ID;
        }

        /// <summary>
        /// 지정된 ID의 도커 이미지를 로컬 저장소에서 삭제합니다. 강제(force) 옵션을 통해 사용 중인 이미지도 지울 수 있습니다.
        /// </summary>
        public async Task DeleteImageAsync(string imageId, bool force = false)
        {
            await _client.Images.DeleteImageAsync(imageId, new ImageDeleteParameters { Force = force });
        }

        /// <summary>
        /// docker export 등으로 만든 root filesystem tar를 새 이미지로 import합니다.
        /// </summary>
        public async Task ImportImageFromTarAsync(string tarFilePath, string repository, string tag, string message)
        {
            await using var tarStream = File.OpenRead(tarFilePath);
            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters
                {
                    FromSrc = "-",
                    Repo = repository,
                    Tag = tag,
                    Message = message
                },
                tarStream,
                null,
                new Progress<JSONMessage>(m => Debug.WriteLine(m.Status ?? m.ErrorMessage ?? m.ProgressMessage)),
                CancellationToken.None);
        }

        /// <summary>
        /// 기존 이미지에 새 repository:tag 별칭을 붙입니다.
        /// </summary>
        public async Task TagImageAsync(string sourceImage, string repository, string tag, bool force = true)
        {
            await _client.Images.TagImageAsync(sourceImage, new ImageTagParameters
            {
                RepositoryName = repository,
                Tag = tag,
                Force = force
            }, CancellationToken.None);
        }

        /// <summary>
        /// 지정한 repository:tag 이미지를 레지스트리에 push합니다.
        /// </summary>
        public async Task PushImageAsync(string repository, string tag, string? username = null, string? password = null, string? serverAddress = null)
        {
            AuthConfig? authConfig = null;
            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
            {
                authConfig = new AuthConfig
                {
                    Username = username,
                    Password = password,
                    ServerAddress = serverAddress
                };
            }

            await _client.Images.PushImageAsync(
                repository,
                new ImagePushParameters { Tag = tag },
                authConfig,
                new Progress<JSONMessage>(m => Debug.WriteLine(m.Status ?? m.ErrorMessage ?? m.ProgressMessage)),
                CancellationToken.None);
        }

        /// <summary>
        /// docker save와 동일하게 이미지 tar 아카이브를 파일로 저장합니다.
        /// </summary>
        public async Task SaveImageAsync(string image, string tarFilePath)
        {
            await using var source = await _client.Images.SaveImageAsync(image, CancellationToken.None);
            await using var target = File.Create(tarFilePath);
            await source.CopyToAsync(target);
        }

        /// <summary>
        /// docker load와 동일하게 docker save tar 아카이브를 로드합니다.
        /// </summary>
        public async Task LoadImageFromTarAsync(string tarFilePath)
        {
            await using var source = File.OpenRead(tarFilePath);
            await _client.Images.LoadImageAsync(
                new ImageLoadParameters { Quiet = false },
                source,
                new Progress<JSONMessage>(m => Debug.WriteLine(m.Status ?? m.ErrorMessage ?? m.ProgressMessage)),
                CancellationToken.None);
        }

        public async Task BuildImageAsync(string targetImageName, string buildContextPath, string dockerfilePath, IProgress<JSONMessage>? progress = null)
        {
            if (string.IsNullOrWhiteSpace(targetImageName))
                throw new ArgumentException("이미지 태그가 비어 있습니다.", nameof(targetImageName));
            if (string.IsNullOrWhiteSpace(buildContextPath) || !Directory.Exists(buildContextPath))
                throw new DirectoryNotFoundException($"빌드 컨텍스트 폴더를 찾을 수 없습니다: {buildContextPath}");
            if (string.IsNullOrWhiteSpace(dockerfilePath) || !File.Exists(dockerfilePath))
                throw new FileNotFoundException("Dockerfile을 찾을 수 없습니다.", dockerfilePath);

            string fullContextPath = Path.GetFullPath(buildContextPath);
            string fullDockerfilePath = Path.GetFullPath(dockerfilePath);
            string dockerfileInContext = Path.GetRelativePath(fullContextPath, fullDockerfilePath).Replace('\\', '/');

            if (dockerfileInContext.StartsWith("../", StringComparison.Ordinal) ||
                Path.IsPathRooted(dockerfileInContext))
            {
                throw new InvalidOperationException("Dockerfile은 빌드 컨텍스트 폴더 안에 있어야 합니다.");
            }

            string tempTarFile = Path.Combine(Path.GetTempPath(), $"DockerDiagramBuildContext_{Guid.NewGuid():N}.tar");
            var errors = new List<string>();

            try
            {
                TarFile.CreateFromDirectory(fullContextPath, tempTarFile, includeBaseDirectory: false);

                await using var tarStream = File.OpenRead(tempTarFile);
                var buildProgress = new Progress<JSONMessage>(message =>
                {
                    string? output = message.ErrorMessage ?? message.Stream ?? message.Status ?? message.ProgressMessage;
                    if (!string.IsNullOrWhiteSpace(output))
                        Debug.WriteLine($"[DockerBuild] {output.TrimEnd()}");

                    if (!string.IsNullOrWhiteSpace(message.ErrorMessage))
                        errors.Add(message.ErrorMessage);

                    progress?.Report(message);
                });

                await _client.Images.BuildImageFromDockerfileAsync(
                    new ImageBuildParameters
                    {
                        Tags = new List<string> { targetImageName },
                        Dockerfile = dockerfileInContext,
                        Remove = true,
                        ForceRemove = true
                    },
                    tarStream,
                    null,
                    null,
                    buildProgress,
                    CancellationToken.None);

                if (errors.Count > 0)
                {
                    throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
                }
            }
            finally
            {
                if (File.Exists(tempTarFile))
                {
                    File.Delete(tempTarFile);
                }
            }
        }

        /// <summary>
        /// 지정된 이름의 도커 볼륨을 영구적으로 삭제합니다.
        /// </summary>
        public async Task RemoveVolumeAsync(string name, bool force = false)
        {
            await _client.Volumes.RemoveAsync(name, force);
        }

        /// <summary>
        /// 지정된 ID의 가상 네트워크 망을 도커 엔진에서 영구적으로 삭제합니다.
        /// </summary>
        public async Task RemoveNetworkAsync(string id)
        {
            await _client.Networks.DeleteNetworkAsync(id);
        }

        /// <summary>
        /// 컨테이너에 실제로 설치된 셸을 먼저 확인한 뒤 시스템 터미널에서 해당 셸만 실행합니다.
        /// </summary>
        public async Task<bool> OpenTerminalAsync(string containerId)
        {
            if (string.IsNullOrWhiteSpace(containerId))
                throw new ArgumentException("Container ID is required.", nameof(containerId));

            var inspect = await _client.Containers.InspectContainerAsync(containerId);
            bool isWindowsContainer = (inspect.Platform ?? string.Empty)
                .Contains("windows", StringComparison.OrdinalIgnoreCase);

            string[][] candidates = isWindowsContainer
                ? new[]
                {
                    new[] { "pwsh", "-NoProfile", "-Command", "exit 0" },
                    new[] { "powershell", "-NoProfile", "-Command", "exit 0" },
                    new[] { "cmd", "/c", "exit 0" }
                }
                : new[]
                {
                    new[] { "/bin/bash", "-c", "exit 0" },
                    new[] { "/bin/sh", "-c", "exit 0" },
                    new[] { "/bin/ash", "-c", "exit 0" }
                };

            foreach (string[] candidate in candidates)
            {
                try
                {
                    var execParams = new ContainerExecCreateParameters
                    {
                        Cmd = candidate,
                        AttachStdout = true,
                        AttachStderr = true,
                        Tty = false
                    };
                    var exec = await _client.Exec.ExecCreateContainerAsync(containerId, execParams);
                    using var stream = await _client.Exec.StartAndAttachContainerExecAsync(exec.ID, false);
                    await stream.CopyOutputToAsync(default, Stream.Null, Stream.Null, CancellationToken.None);
                    var status = await _client.Exec.InspectContainerExecAsync(exec.ID);
                    if (status.ExitCode != 0) continue;
                }
                catch (DockerApiException)
                {
                    continue;
                }

                string shell = candidate[0];
                var processInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/K \"docker exec -it {containerId} {shell}\"",
                    UseShellExecute = true
                };
                Process.Start(processInfo);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 실행 중인 컨테이너 내부에서 특정 명령어(Command)를 백그라운드에서 비동기적으로 실행하고, 그 결과를 디버그 로그로 기록합니다.
        /// </summary>
        public async Task ExecuteCommandAsync(string containerId, string command)
        {
            try
            {
                var result = await ExecuteCommandWithOutputAsync(containerId, command);

                if (!string.IsNullOrWhiteSpace(result.Stdout))
                    Debug.WriteLine($"[DockerDiscovery] Exec Output:\n{result.Stdout}");

                if (!string.IsNullOrWhiteSpace(result.Stderr))
                    Debug.WriteLine($"[DockerDiscovery] Exec Error:\n{result.Stderr}");

                if (result.ExitCode != 0)
                {
                    Debug.WriteLine($"[DockerDiscovery] Exec Failed (Code: {result.ExitCode})");
                }
                else
                {
                    Debug.WriteLine($"[DockerDiscovery] Exec Success");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DockerDiscovery] ExecuteCommandAsync Error: {ex.Message}");
            }
        }

        /// <summary>
        /// 실행 중인 컨테이너 내부에서 명령어를 실행하고 stdout/stderr/exit code를 호출자에게 반환합니다.
        /// </summary>
        public async Task<ExecCommandResult> ExecuteCommandWithOutputAsync(string containerId, string command)
        {
            var inspect = await _client.Containers.InspectContainerAsync(containerId);
            string platform = inspect.Platform ?? string.Empty;
            string[] cmdShell = platform.Contains("windows", StringComparison.OrdinalIgnoreCase)
                ? new[] { "cmd", "/c", command }
                : new[] { "/bin/sh", "-c", command };

            var execParams = new ContainerExecCreateParameters
            {
                Cmd = cmdShell,
                AttachStdout = true,
                AttachStderr = true,
                Tty = false
            };

            var execCreateResp = await _client.Exec.ExecCreateContainerAsync(containerId, execParams);

            using var stream = await _client.Exec.StartAndAttachContainerExecAsync(execCreateResp.ID, false);
            using var stdoutMs = new MemoryStream();
            using var stderrMs = new MemoryStream();

            await stream.CopyOutputToAsync(default, stdoutMs, stderrMs, CancellationToken.None);

            stdoutMs.Position = 0;
            stderrMs.Position = 0;

            var finalStatus = await _client.Exec.InspectContainerExecAsync(execCreateResp.ID);

            return new ExecCommandResult
            {
                ExitCode = finalStatus.ExitCode,
                Stdout = Encoding.UTF8.GetString(stdoutMs.ToArray()),
                Stderr = Encoding.UTF8.GetString(stderrMs.ToArray())
            };
        }

        /// <summary>
        /// 특정 컨테이너를 지정된 가상 네트워크 망에 연결(편입)시킵니다. 필요시 정적 IP를 할당할 수 있습니다.
        /// </summary>
        public async Task ConnectNetworkAsync(string networkId, string containerId, ContainerNetworkOptions? options = null)
        {
            try
            {
                var config = new NetworkConnectParameters
                {
                    Container = containerId
                };

                if (options != null && options.HasAnyOption)
                {
                    config.EndpointConfig = new EndpointSettings
                    {
                        IPAMConfig = new EndpointIPAMConfig
                        {
                            IPv4Address = string.IsNullOrWhiteSpace(options.StaticIPv4) ? null : options.StaticIPv4,
                            IPv6Address = string.IsNullOrWhiteSpace(options.StaticIPv6) ? null : options.StaticIPv6
                        },
                        Aliases = options.Aliases.Where(alias => !string.IsNullOrWhiteSpace(alias)).Select(alias => alias.Trim()).ToList(),
                        DriverOpts = options.DriverOptions.Count > 0 ? options.DriverOptions : null
                    };
                }

                await _client.Networks.ConnectNetworkAsync(networkId, config);

                Debug.WriteLine($"[DockerDiscovery] Network Connected: {networkId} -> {containerId} (IPv4: {options?.StaticIPv4 ?? "Auto"}, IPv6: {options?.StaticIPv6 ?? "Auto"})");
            }
            catch (DockerApiException ex)
            {
                if (ex.Message.Contains("already exists") || ex.Message.Contains("address already in use"))
                {
                    throw new Exception($"네트워크 연결 실패: 이미 연결되어 있거나 지정한 IP가 다른 컨테이너에서 사용 중입니다.");
                }
                throw;
            }
        }

        /// <summary>
        /// 특정 컨테이너를 지정된 가상 네트워크 망에서 강제로 연결 해제시킵니다.
        /// </summary>
        public async Task DisconnectNetworkAsync(string networkId, string containerId)
        {
            await _client.Networks.DisconnectNetworkAsync(networkId, new NetworkDisconnectParameters
            {
                Container = containerId,
                Force = true
            });
        }

        private class SyncProgress<T> : IProgress<T>
        {
            private readonly Action<T> _handler;
            public SyncProgress(Action<T> handler) => _handler = handler;
            public void Report(T value) => _handler(value);
        }

        /// <summary>
        /// 특정 컨테이너의 실시간 리소스 사용량(CPU 퍼센트, 메모리 사용량 및 제한)을 측정하여 반환합니다.
        /// </summary>
        public async Task<ContainerStats> GetContainerStatsAsync(string containerId)
        {
            int timeoutSeconds = CurrentProfile.Type == EndpointType.SshRemote ? 10 : 3;

            try
            {
                var statsParams = new ContainerStatsParameters { Stream = false };
                ContainerStatsResponse? stats = null;

                var progress = new SyncProgress<ContainerStatsResponse>(r => stats = r);

                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                {
                    // API 호출이 끝날 때까지 대기
                    await _client.Containers.GetContainerStatsAsync(containerId, statsParams, progress, cts.Token);

                    if (stats != null)
                    {
                        return new ContainerStats
                        {
                            CpuPercentage = CalculateCpuPercentage(stats),
                            MemoryUsedMB = stats.MemoryStats.Usage / (1024.0 * 1024.0),
                            MemoryLimitMB = stats.MemoryStats.Limit / (1024.0 * 1024.0)
                        };
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine($"[DockerStats] {containerId}의 통계 데이터 조회 시간({timeoutSeconds}초)이 초과되었습니다.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DockerStats] Stats Error: {ex.Message}");
            }

            return new ContainerStats();
        }

        /// <summary>
        /// 도커 API로부터 전달받은 컨테이너 통계(Stats) 데이터를 바탕으로 실시간 CPU 사용률(%)을 계산합니다.
        /// 도커의 CPU 측정 방식에 따라, 이전 측정값(PreCPUStats)과 현재 측정값(CPUStats)의 차이(Delta)를 구한 뒤
        /// 전체 시스템 CPU 사용량 대비 컨테이너가 점유한 비율을 계산하고 코어 수(OnlineCPUs)를 곱하여 최종 퍼센티지를 도출합니다.
        /// </summary>
        private double CalculateCpuPercentage(ContainerStatsResponse stats)
        {
            // 컨테이너 자체의 CPU 사용량 변화량
            double cpuDelta = stats.CPUStats.CPUUsage.TotalUsage - stats.PreCPUStats.CPUUsage.TotalUsage;
            // 호스트 시스템 전체의 CPU 사용량 변화량
            double systemDelta = stats.CPUStats.SystemUsage - stats.PreCPUStats.SystemUsage;

            if (systemDelta > 0.0 && cpuDelta > 0.0)
            {
                // (컨테이너 변화량 / 시스템 변화량) * 코어 수 * 100 = 최종 CPU 점유율(%)
                return (cpuDelta / systemDelta) * stats.CPUStats.OnlineCPUs * 100.0;
            }

            return 0.0;
        }

        /// <summary>
        /// 메모리 누수 방지 및 네트워크 소켓 고갈을 막기 위한 표준 IDisposable 패턴 구현부입니다.
        /// 이 서비스 클래스가 소멸될 때, 도커 엔진과 통신하기 위해 열어두었던 HTTP 클라이언트(_client) 등 관리되는(Managed) 리소스를 안전하게 해제합니다.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // HttpClient 등 내부적으로 사용하던 IDisposable 객체들을 명시적으로 해제
                    _client?.Dispose();
                    _systemDiskUsageCacheLock.Dispose();
                    _systemInfoCacheLock.Dispose();
                }
                _disposedValue = true;
            }
        }

        /// <summary>
        /// 사용이 끝난 도커 클라이언트 객체의 네트워크 통신 리소스를 안전하게 해제(메모리 누수 방지)합니다.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 특정 컨테이너가 뱉어낸 최근 로그(표준 출력 및 에러)를 지정된 줄(Tail) 수만큼 가져와 문자열로 반환합니다.
        /// </summary>
        public async Task<string> GetContainerLogsAsync(string containerId, int tailCount = 500)
        {
            try
            {
                var parameters = new ContainerLogsParameters
                {
                    ShowStdout = true,
                    ShowStderr = true,
                    Tail = tailCount <= 0 ? "all" : tailCount.ToString(),
                    Timestamps = true // 도커 엔진에서 각 줄 앞에 타임스탬프를 붙여서 보내줌
                };

                using (var stream = await _client.Containers.GetContainerLogsAsync(containerId, false, parameters))
                {
                    using (var stdoutMs = new MemoryStream())
                    using (var stderrMs = new MemoryStream())
                    {
                        await stream.CopyOutputToAsync(default, stdoutMs, stderrMs, CancellationToken.None);

                        stdoutMs.Position = 0;
                        stderrMs.Position = 0;

                        string stdout = System.Text.Encoding.UTF8.GetString(stdoutMs.ToArray());
                        string stderr = System.Text.Encoding.UTF8.GetString(stderrMs.ToArray());

                        // 원본 로그 문자열 합치기
                        string rawLogs = stdout + stderr;

                        // Docker의 UTC 시각을 로컬 표시 형식으로 변환합니다.
                        return FormatDockerLogs(rawLogs);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DockerDiscovery] GetContainerLogsAsync Error: {ex.Message}");
                return $"로그를 가져오는 중 오류가 발생했습니다:\n{ex.Message}";
            }
        }

        /// <summary>
        /// 도커 엔진과 영구적인 파이프(MultiplexedStream)를 맺고, 컨테이너가 로그를 뱉을 때마다 onLogLineReceived 콜백으로 한 뭉치씩 쏴줍니다.
        /// </summary>
        public async Task StreamContainerLogsAsync(string containerId, Action<string> onLogLineReceived, CancellationToken cancellationToken, int initialTailCount = 100)
        {
            var parameters = new ContainerLogsParameters
            {
                ShowStdout = true,
                ShowStderr = true,
                Follow = true,
                Tail = Math.Max(0, initialTailCount).ToString(),
                Timestamps = true
            };

            try
            {
                using (var stream = await _client.Containers.GetContainerLogsAsync(containerId, false, parameters, cancellationToken))
                {
                    var buffer = new byte[81920]; // 80KB 버퍼

                    // 사용자가 창을 닫을 때까지(cancellationToken) 무한 대기
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);

                        if (result.EOF) break; // 컨테이너가 꺼지면 탈출

                        string rawChunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        string formattedChunk = FormatDockerLogs(rawChunk);

                        // UI 쪽으로 "로그 뭉치 왔어요!" 하고 쏴줌
                        if (!string.IsNullOrWhiteSpace(formattedChunk))
                        {
                            onLogLineReceived?.Invoke(formattedChunk);
                        }
                    }
                }
            }
            catch (TaskCanceledException)
            {
                Debug.WriteLine($"[VDM Stream] {containerId} 로그 파이프 안전 종료 완료.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VDM Stream] 로그 스트리밍 중 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// 도커 엔진이 반환한 원시 로그의 타임스탬프(ISO 8601)를 파싱하여 보기 편한 로컬 시간 포맷으로 가공합니다.
        /// </summary>
        private string FormatDockerLogs(string rawLogs)
        {
            if (string.IsNullOrWhiteSpace(rawLogs)) return string.Empty;

            var sb = new System.Text.StringBuilder();

            // 줄 단위로 분리
            var lines = rawLogs.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                // 타임스탬프와 로그 메시지 사이에는 공백(' ')이 존재함
                int spaceIndex = line.IndexOf(' ');

                if (spaceIndex > 0)
                {
                    string timePart = line.Substring(0, spaceIndex);

                    // 도커의 UTC 타임스탬프 문자열을 DateTime 객체로 변환 시도
                    if (DateTime.TryParse(timePart, out DateTime dt))
                    {
                        // 로컬 시간으로 변경 후, [2024-04-03 14:30:22] 형식으로 가공
                        string formattedTime = dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                        string message = line.Substring(spaceIndex + 1);

                        sb.AppendLine($"[{formattedTime}] {message}");
                    }
                    else
                    {
                        // 파싱 실패 시 원본 그대로 출력
                        sb.AppendLine(line);
                    }
                }
                else
                {
                    // 공백이 없는 줄(예상치 못한 포맷)도 원본 유지
                    sb.AppendLine(line);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 도커 허브(Docker Hub)에서 이미지를 검색하여 결과를 반환합니다.
        /// </summary>
        public async Task<List<ImageSearchResponse>> SearchImagesAsync(string term, int limit = 20)
        {
            var parameters = new ImagesSearchParameters
            {
                Term = term,
                Limit = limit
            };

            var results = await _client.Images.SearchImagesAsync(parameters);
            return results.ToList();
        }

        public Task<ImageInspectResponse> InspectImageAsync(
            string imageReference,
            CancellationToken cancellationToken = default)
        {
            return _client.Images.InspectImageAsync(imageReference, cancellationToken);
        }

        public async Task<ContainerImageMetadata?> GetImageMetadataAsync(
            string imageReference,
            string? username = null,
            string? password = null,
            string? serverAddress = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var inspect = await _client.Images.InspectImageAsync(imageReference, cancellationToken);
                return new ContainerImageMetadata
                {
                    ImageReference = imageReference,
                    Source = "Docker Engine",
                    Environment = inspect.Config?.Env?.ToList() ?? [],
                    ExposedPorts = inspect.Config?.ExposedPorts?.Keys.ToList() ?? [],
                    Volumes = inspect.Config?.Volumes?.Keys.ToList() ?? [],
                    Entrypoint = inspect.Config?.Entrypoint?.ToList() ?? [],
                    Command = inspect.Config?.Cmd?.ToList() ?? []
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return await RegistryImageMetadataReader.ReadAsync(
                    imageReference,
                    username,
                    password,
                    serverAddress,
                    cancellationToken);
            }
        }

        /// <summary>
        /// 진행률(Progress)을 UI로 실시간 보고하면서 지정된 이미지와 태그를 도커 허브에서 다운로드(Pull)합니다.
        /// </summary>
        public async Task PullImageWithProgressAsync(string image, string tag, IProgress<JSONMessage> progress, string? username = null, string? password = null, string? serverAddress = null)
        {
            AuthConfig? authConfig = null;

            // 아이디와 비밀번호가 모두 전달된 경우에만 프라이빗 레지스트리 인증 객체 생성
            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
            {
                authConfig = new AuthConfig
                {
                    Username = username,
                    Password = password,
                    ServerAddress = string.IsNullOrWhiteSpace(serverAddress) ? "https://index.docker.io/v1/" : serverAddress
                };
            }

            // Docker Engine이 전달하는 다운로드 진행률을 호출자에게 전달합니다.
            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = image, Tag = string.IsNullOrWhiteSpace(tag) ? "latest" : tag },
                authConfig,
                progress);
        }

        /// <summary>
        /// 특정 도커 볼륨을 임시 알파인(Alpine) 컨테이너에 마운트하여 .tar 파일로 압축한 뒤 로컬 PC에 저장(백업)합니다.
        /// </summary>
        public async Task BackupVolumeAsync(string volumeName, string hostTarFilePath)
        {
            string tempContainerName = $"vdm-backup-{Guid.NewGuid().ToString().Substring(0, 6)}";

            // 1. 임시로 사용할 가벼운 alpine 이미지 다운로드 (이미 있으면 즉시 넘어감)
            await PullImageAsync("alpine", "latest");

            // 2. 임시 컨테이너 생성 및 시작 (백업할 볼륨을 컨테이너 내부의 /backup_data 에 마운트)
            string containerId = await CreateAndStartContainerAsync(
                name: tempContainerName,
                image: "alpine",
                tag: "latest",
                ports: new List<string>(),
                envs: new List<string>(),
                volumes: new List<string> { $"{volumeName}:/backup_data" },
                restartPolicy: "no",
                memoryMb: 0,
                cpuCount: 0,
                command: "sleep 300", // 복사하는 동안 컨테이너가 죽지 않도록 300초 대기
                tty: false
            );

            try
            {
                // 3. 컨테이너 내부의 /backup_data 폴더 자체를 TAR 스트림으로 가져와서 로컬 파일로 저장
                var tarResponse = await _client.Containers.GetArchiveFromContainerAsync(containerId, new GetArchiveFromContainerParameters
                {
                    Path = "/backup_data"
                }, false);

                using (var fileStream = File.Create(hostTarFilePath))
                {
                    await tarResponse.Stream.CopyToAsync(fileStream);
                }
            }
            finally
            {
                // 4. 백업이 끝나면(성공하든 에러가 나든) 흔적 없이 임시 컨테이너 강제 삭제
                await _client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true });
            }
        }

        /// <summary>
        /// 로컬 PC의 .tar 백업 파일을 읽어, 임시 컨테이너를 통해 특정 도커 볼륨에 데이터를 덮어씁니다(복원).
        /// </summary>
        public async Task RestoreVolumeAsync(string volumeName, string hostTarFilePath)
        {
            string tempContainerName = $"vdm-restore-{Guid.NewGuid().ToString().Substring(0, 6)}";

            await PullImageAsync("alpine", "latest");

            string containerId = await CreateAndStartContainerAsync(
                name: tempContainerName,
                image: "alpine",
                tag: "latest",
                ports: new List<string>(),
                envs: new List<string>(),
                volumes: new List<string> { $"{volumeName}:/backup_data" },
                restartPolicy: "no",
                memoryMb: 0,
                cpuCount: 0,
                command: "sleep 300",
                tty: false
            );

            try
            {
                // 로컬의 .tar 파일을 읽어서 컨테이너 내부의 볼륨 마운트 경로(/backup_data)에 압축 해제하며 덮어쓰기
                using (var fs = File.OpenRead(hostTarFilePath))
                {
                    await _client.Containers.ExtractArchiveToContainerAsync(containerId, new ContainerPathStatParameters
                    {
                        Path = "/", // 압축 해제 기준 경로 (tar 내부에 backup_data 폴더가 포함되어 있으므로 최상단에 품)
                        AllowOverwriteDirWithFile = true
                    }, fs);
                }
            }
            finally
            {
                // 복원 후 임시 컨테이너 삭제
                await _client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true });
            }
        }

        /// <summary>
        /// 실행 중인 컨테이너를 중단하지 않고 CPU 및 메모리 제한(Limit)을 실시간으로 동적 변경합니다.
        /// </summary>
        public async Task UpdateContainerResourcesAsync(string containerId, double cpuCount, long memoryMb)
        {
            var updateParams = new ContainerUpdateParameters();

            // CPU 설정 (NanoCPUs: 1 코어 = 1,000,000,000 단위)
            if (cpuCount > 0)
            {
                updateParams.NanoCPUs = (long)(cpuCount * 1_000_000_000);
            }

            // 메모리 설정 (Bytes 단위로 변환)
            if (memoryMb > 0)
            {
                long memoryBytes = memoryMb * 1024 * 1024;
                updateParams.Memory = memoryBytes;

                // 메모리 제한 변경 시 Docker 규칙에 맞춰 swap 제한도 함께 지정합니다.
                // 여기서는 메모리와 Swap을 동일하게 주어(하드 리미트) 칼같이 제한하도록 설정합니다.
                updateParams.MemorySwap = memoryBytes;
            }

            // 도커 API를 찔러서 즉시 업데이트 (재시작 불필요!)
            await _client.Containers.UpdateContainerAsync(containerId, updateParams);
        }

        /// <summary>
        /// 도커 엔진(Daemon)의 실제 시스템 스펙(사용 가능한 CPU 코어 수, 전체 물리 메모리 등)을 조회합니다.
        /// </summary>
        public async Task<SystemInfoResponse> GetSystemInfoAsync()
        {
            if (_systemInfoCache != null &&
                DateTimeOffset.UtcNow - _systemInfoCacheAt < SystemInfoCacheTtl)
            {
                return _systemInfoCache;
            }

            await _systemInfoCacheLock.WaitAsync();
            try
            {
                if (_systemInfoCache != null &&
                    DateTimeOffset.UtcNow - _systemInfoCacheAt < SystemInfoCacheTtl)
                {
                    return _systemInfoCache;
                }

                _systemInfoCache = await _client.System.GetSystemInfoAsync();
                _systemInfoCacheAt = DateTimeOffset.UtcNow;
                return _systemInfoCache;
            }
            finally
            {
                _systemInfoCacheLock.Release();
            }
        }

        private sealed class SwarmServiceResponse
        {
            public string ID { get; set; } = string.Empty;
            public SwarmServiceSpecResponse? Spec { get; set; }
            public SwarmServiceEndpointResponse? Endpoint { get; set; }
            public SwarmServiceStatusResponse? ServiceStatus { get; set; }
        }

        private sealed class SwarmServiceSpecResponse
        {
            public string Name { get; set; } = string.Empty;
            public Dictionary<string, string>? Labels { get; set; }
            public SwarmServiceTaskTemplateResponse? TaskTemplate { get; set; }
            public SwarmServiceModeResponse? Mode { get; set; }
        }

        private sealed class SwarmServiceTaskTemplateResponse
        {
            public SwarmServiceContainerSpecResponse? ContainerSpec { get; set; }
        }

        private sealed class SwarmServiceContainerSpecResponse
        {
            public string Image { get; set; } = string.Empty;
        }

        private sealed class SwarmServiceModeResponse
        {
            public SwarmServiceReplicatedModeResponse? Replicated { get; set; }
            public object? Global { get; set; }
        }

        private sealed class SwarmServiceReplicatedModeResponse
        {
            public ulong Replicas { get; set; }
        }

        private sealed class SwarmServiceEndpointResponse
        {
            public SwarmServiceEndpointSpecResponse? Spec { get; set; }
        }

        private sealed class SwarmServiceEndpointSpecResponse
        {
            public List<SwarmServicePortResponse>? Ports { get; set; }
        }

        private sealed class SwarmServicePortResponse
        {
            public string Protocol { get; set; } = string.Empty;
            public uint TargetPort { get; set; }
            public uint PublishedPort { get; set; }
        }

        private sealed class SwarmServiceStatusResponse
        {
            public ulong RunningTasks { get; set; }
            public ulong DesiredTasks { get; set; }
        }

        private sealed class SwarmTaskResponse
        {
            public string ID { get; set; } = string.Empty;
            public ulong Slot { get; set; }
            public string NodeID { get; set; } = string.Empty;
            public string DesiredState { get; set; } = string.Empty;
            public SwarmServiceTaskTemplateResponse? Spec { get; set; }
            public SwarmTaskStatusResponse? Status { get; set; }
        }

        private sealed class SwarmTaskStatusResponse
        {
            public string State { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
            public string Err { get; set; } = string.Empty;
            public SwarmTaskContainerStatusResponse? ContainerStatus { get; set; }
        }

        private sealed class SwarmTaskContainerStatusResponse
        {
            public string ContainerID { get; set; } = string.Empty;
        }

        private sealed class SwarmNodeResponse
        {
            public string ID { get; set; } = string.Empty;
            public SwarmNodeSpecResponse? Spec { get; set; }
            public SwarmNodeDescriptionResponse? Description { get; set; }
            public SwarmNodeStatusResponse? Status { get; set; }
            public SwarmNodeManagerStatusResponse? ManagerStatus { get; set; }
        }

        private sealed class SwarmNodeSpecResponse
        {
            public string Role { get; set; } = string.Empty;
            public string Availability { get; set; } = string.Empty;
        }

        private sealed class SwarmNodeDescriptionResponse
        {
            public string Hostname { get; set; } = string.Empty;
            public SwarmNodeEngineResponse? Engine { get; set; }
        }

        private sealed class SwarmNodeEngineResponse
        {
            public string EngineVersion { get; set; } = string.Empty;
        }

        private sealed class SwarmNodeStatusResponse
        {
            public string State { get; set; } = string.Empty;
            public string Addr { get; set; } = string.Empty;
        }

        private sealed class SwarmNodeManagerStatusResponse
        {
            public bool Leader { get; set; }
            public string Reachability { get; set; } = string.Empty;
        }
    }
}
