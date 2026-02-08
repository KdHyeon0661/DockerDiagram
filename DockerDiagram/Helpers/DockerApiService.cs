using Docker.DotNet;
using Docker.DotNet.Models;
using DockerDiagram.Models;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.Runtime.InteropServices;

namespace DockerDiagram.Helpers
{
    public class DockerApiService : IContainerService, IVolumeService, INetworkService, IImageService, ISystemService
    {
        private readonly DockerClient _client;

        public DockerApiService()
        {
            var dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new Uri("npipe://./pipe/docker_engine") : new Uri("unix:///var/run/docker.sock");
            var config = new DockerClientConfiguration(dockerUri);
            _client = config.CreateClient();
        }

        // =========================================================
        // 1. ISystemService 구현
        // =========================================================

        // 도커 데몬에 핑을 날려서 연결 확인
        public async Task<bool> PingAsync()
        {
            try
            {
                await _client.System.PingAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }

        // =========================================================
        // 2. IContainerService 구현
        // =========================================================

        // 도커 컨테이너 목록 조회
        public async Task<List<DockerContainer>> GetContainersAsync()
        {
            var containers = await _client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true }
            ); // 모든 컨테이너 조회. false로 하면 running 상태만 조회

            var result = new List<DockerContainer>();
            foreach (var c in containers)
            {
                string portStr = "";
                if (c.Ports != null) // 포트 매핑 정보가 있을 경우
                {
                    var ports = new List<string>();
                    foreach (var p in c.Ports ?? Enumerable.Empty<Docker.DotNet.Models.Port>()) // 각 포트 매핑 정보를 문자열로 변환
                    {
                        var proto = string.IsNullOrWhiteSpace(p.Type) ? "tcp" : p.Type;

                        if (p.PublicPort > 0) // Host에 Exposed 된 포트가 있는 경우
                        {
                            var ipPrefix = string.IsNullOrWhiteSpace(p.IP) ? "" : $"{p.IP}:";
                            ports.Add($"{ipPrefix}{p.PublicPort}->{p.PrivatePort}/{proto}");
                        }
                        else // Exposed 된 포트만 있는 경우
                        {
                            ports.Add($"{p.PrivatePort}/{proto}");
                        }
                    }
                    portStr = string.Join(", ", ports);
                }

                result.Add(new DockerContainer // 도커 컨테이너 정보를 커스텀 모델로 매핑
                {
                    Id = c.ID,
                    Name = c.Names[0].TrimStart('/'),
                    Image = c.Image,
                    State = c.State,
                    Ports = portStr,
                });
            }
            return result;
        }

        // =========================================================
        // 3. IImageService 구현
        // =========================================================

        // 도커 이미지 목록 조회
        public async Task<List<DockerImage>> GetImagesAsync()
        {
            var images = await _client.Images.ListImagesAsync(new ImagesListParameters { All = false }); // intermediate 이미지 제외
            var result = new List<DockerImage>();
            foreach (var img in images)
            {
                string repoTag = (img.RepoTags != null && img.RepoTags.Count > 0) ? img.RepoTags[0] : "<none>:<none>";

                int lastColonIndex = repoTag.LastIndexOf(':');
                string repository = lastColonIndex > 0 ? repoTag.Substring(0, lastColonIndex) : repoTag;
                string tag = lastColonIndex > 0 ? repoTag.Substring(lastColonIndex + 1) : "<none>";

                result.Add(new DockerImage
                {
                    Id = img.ID,
                    Repository = repository,
                    Tag = tag,
                    Size = img.Size
                });
            }
            return result;
        }

        // =========================================================
        // 4. IVolumeService 구현
        // =========================================================

        // ★ [수정 2] 반환 타입: List<DockerVolume> + 매핑 수정
        // 도커 볼륨 목록 조회
        public async Task<List<DockerVolume>> GetVolumesAsync()
        {
            var volumes = await _client.Volumes.ListAsync();
            return volumes.Volumes.Select(v => new DockerVolume
            {
                Name = v.Name,
                Id = v.Name, // 볼륨은 이름이 곧 ID
            }).ToList();
        }

        // =========================================================
        // 5. INetworkService 구현
        // =========================================================

        // ★ [수정 3] 반환 타입: List<DockerGroup> + 매핑 수정
        // 도커 네트워크 목록 조회
        public async Task<List<DockerGroup>> GetNetworksAsync()
        {
            var networks = await _client.Networks.ListNetworksAsync();
            return networks.Select(n => new DockerGroup
            {
                Name = n.Name,
                Id = n.ID, // ID 매핑
                Type = GroupType.Network, // ★ NodeType.Network -> GroupType.Network 변경
                Driver = n.Driver // ★ Image -> Driver 속성으로 변경
            }).ToList();
        }

        // ---------------------------------------------------------
        // 아래부터는 원본 로직 100% 유지 (인터페이스 구현)
        // ---------------------------------------------------------

        // 컨테이너 상세 정보 조회
        public async Task<ContainerInspectResponse> InspectContainerAsync(string containerId)
        {
            return await _client.Containers.InspectContainerAsync(containerId);
        }

        // 볼륨 상세 정보 조회
        public async Task<VolumeResponse> InspectVolumeAsync(string name)
        {
            return await _client.Volumes.InspectAsync(name);
        }

        // 네트워크 상세 정보 조회
        public async Task<NetworkResponse> InspectNetworkAsync(string networkId)
        {
            return await _client.Networks.InspectNetworkAsync(networkId);
        }

        // 이 볼륨을 사용 중인 컨테이너 목록 찾기
        public async Task<List<string>> GetContainersUsingVolumeAsync(string volumeName)
        {
            var containers = await _client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true });

            var result = new List<string>();

            foreach (var c in containers)
            {
                if (c.Mounts == null) continue;

                if (c.Mounts.Any(m => m.Name == volumeName || m.Source.EndsWith(volumeName)))
                {
                    string name = c.Names[0].TrimStart('/');
                    result.Add(name);
                }
            }
            return result;
        }

        // 컨테이너 시작
        public async Task StartContainerAsync(string id)
            => await _client.Containers.StartContainerAsync(id, new ContainerStartParameters());

        // 컨테이너 중지
        public async Task StopContainerAsync(string id)
            => await _client.Containers.StopContainerAsync(id, new ContainerStopParameters { WaitBeforeKillSeconds = 5 });

        // 컨테이너 일시정지
        public async Task PauseContainerAsync(string id)
            => await _client.Containers.PauseContainerAsync(id);

        // 컨테이너 일시정지 해제
        public async Task UnpauseContainerAsync(string id)
            => await _client.Containers.UnpauseContainerAsync(id);

        // 컨테이너 재시작
        public async Task RestartContainerAsync(string id)
            => await _client.Containers.RestartContainerAsync(id, new ContainerRestartParameters());

        // 컨테이너 삭제
        public async Task RemoveContainerAsync(string id)
        {
            await _client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true, RemoveVolumes = false });
        }

        // 이미지 풀링
        public async Task PullImageAsync(string image, string tag)
        {
            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = image, Tag = tag },
                null,
                new Progress<JSONMessage>());
        }

        // 컨테이너 생성 및 시작 (원본 로직 유지)
        public async Task<string> CreateAndStartContainerAsync(
            string name, string image, string tag, List<string> ports, List<string> envs, List<string> volumes,
            string restartPolicy, long memoryMb, double cpuCount)
        {
            if (image.Contains(":"))
            {
                int lastColon = image.LastIndexOf(':');
                tag = image.Substring(lastColon + 1);
                image = image.Substring(0, lastColon);
            }
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
                    }
                }
            };

            var response = await _client.Containers.CreateContainerAsync(parameters);
            await _client.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());
            return response.ID;
        }

        // 호스트 -> 컨테이너 복사
        public async Task CopyFromContainerAsync(string containerId, string containerPath, string hostPath)
        {
            var tarResponse = await _client.Containers.GetArchiveFromContainerAsync(containerId, new GetArchiveFromContainerParameters
            {
                Path = containerPath
            }, false);

            TarFile.ExtractToDirectory(tarResponse.Stream, hostPath, overwriteFiles: true);
        }

        // 호스트 <- 컨테이너 복사
        public async Task CopyToContainerAsync(string containerId, string hostPath, string containerPath)
        {
            string tempTarFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tar");

            try
            {
                TarFile.CreateFromDirectory(sourceDirectoryName: hostPath, destinationFileName: tempTarFile, includeBaseDirectory: false);

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

        // 볼륨 생성
        public async Task CreateVolumeAsync(string name, string driver)
        {
            await _client.Volumes.CreateAsync(new VolumesCreateParameters
            {
                Name = name,
                Driver = driver
            });
        }

        // 네트워크 생성
        public async Task<string> CreateNetworkAsync(string name, string driver)
        {
            var response = await _client.Networks.CreateNetworkAsync(new NetworksCreateParameters
            {
                Name = name,
                Driver = driver,
                CheckDuplicate = true
            });
            return response.ID;
        }

        // 이미지 삭제
        public async Task DeleteImageAsync(string imageId, bool force = false)
        {
            await _client.Images.DeleteImageAsync(imageId, new ImageDeleteParameters { Force = force });
        }

        // 볼륨 삭제
        public async Task RemoveVolumeAsync(string name)
        {
            await _client.Volumes.RemoveAsync(name, false);
        }

        // 네트워크 삭제
        public async Task RemoveNetworkAsync(string id)
        {
            await _client.Networks.DeleteNetworkAsync(id);
        }

        // 터미널 열기
        public void OpenTerminal(string containerId)
        {
            string commands = $"docker exec -it {containerId} /bin/bash || " +
                              $"docker exec -it {containerId} sh || " +
                              $"docker exec -it {containerId} cmd || " +
                              $"docker exec -it {containerId} powershell";

            var processInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/K \"{commands}\"",
                UseShellExecute = true
            };
            Process.Start(processInfo);
        }

        // 명령어 실행
        public async Task ExecuteCommandAsync(string containerId, string command)
        {
            try
            {
                var inspect = await _client.Containers.InspectContainerAsync(containerId);
                string os = inspect.Platform;

                string[] cmdShell;
                if (os.Contains("windows", StringComparison.OrdinalIgnoreCase))
                {
                    cmdShell = new[] { "cmd", "/c", command };
                }
                else
                {
                    cmdShell = new[] { "/bin/sh", "-c", command };
                }

                var execParams = new ContainerExecCreateParameters
                {
                    Cmd = cmdShell,
                    AttachStdout = true,
                    AttachStderr = true,
                };

                var execCreateResp = await _client.Exec.ExecCreateContainerAsync(containerId, execParams);

                await _client.Exec.StartContainerExecAsync(execCreateResp.ID, default);

                while (true)
                {
                    var status = await _client.Exec.InspectContainerExecAsync(execCreateResp.ID);
                    if (!status.Running)
                    {
                        if (status.ExitCode != 0)
                        {
                            Console.WriteLine($"명령 실행 실패 (Code: {status.ExitCode}): {command}");
                        }
                        else
                        {
                            Console.WriteLine($"명령 실행 성공: {command}");
                        }
                        break;
                    }
                    await Task.Delay(100);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DockerDiscovery] ExecuteCommandAsync 에러: {ex.Message}");
            }
        }

        // 네트워크 연결
        public async Task ConnectNetworkAsync(string networkId, string containerId, string? staticIp = null)
        {
            try
            {
                var config = new NetworkConnectParameters
                {
                    Container = containerId
                };

                if (!string.IsNullOrWhiteSpace(staticIp))
                {
                    config.EndpointConfig = new EndpointSettings
                    {
                        IPAMConfig = new EndpointIPAMConfig
                        {
                            IPv4Address = staticIp
                        }
                    };
                }

                await _client.Networks.ConnectNetworkAsync(networkId, config);

                Console.WriteLine($"Network Connected: {networkId} -> {containerId} (IP: {staticIp ?? "Auto"})");
            }
            catch (DockerApiException ex)
            {
                if (ex.Message.Contains("already exists") || ex.Message.Contains("address already in use"))
                {
                    throw new Exception($"네트워크 연결 실패: 이미 연결되어 있거나 IP({staticIp})가 다른 컨테이너에서 사용 중입니다.");
                }
                throw;
            }
        }

        // 네트워크 해제
        public async Task DisconnectNetworkAsync(string networkId, string containerId)
        {
            await _client.Networks.DisconnectNetworkAsync(networkId, new NetworkDisconnectParameters
            {
                Container = containerId,
                Force = true
            });
        }

        public async Task<ContainerStats> GetContainerStatsAsync(string containerId)
        {
            try
            {
                // 1회성 통계를 가져오기 위해 Stream을 false로 설정 (docker stats --no-stream과 동일)
                var statsParams = new ContainerStatsParameters { Stream = false };

                // CancellationToken을 사용하여 첫 번째 결과만 받고 바로 종료되도록 처리
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    ContainerStatsResponse? stats = null;

                    // GetContainerStatsAsync는 Progress를 통해 데이터를 전달합니다.
                    var progress = new Progress<ContainerStatsResponse>(r => stats = r);

                    await _client.Containers.GetContainerStatsAsync(containerId, statsParams, progress, cts.Token);

                    // 데이터가 도착할 때까지 아주 잠깐 대기
                    while (stats == null && !cts.IsCancellationRequested) await Task.Delay(10);

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
            catch (Exception ex)
            {
                Console.WriteLine($"[Stats] 에러: {ex.Message}");
            }

            return new ContainerStats();
        }

        // 도커 엔진이 주는 복잡한 수치를 퍼센트(%)로 변환하는 공식
        private double CalculateCpuPercentage(ContainerStatsResponse stats)
        {
            // 이전 CPU 값과 현재 값의 차이를 계산
            double cpuDelta = stats.CPUStats.CPUUsage.TotalUsage - stats.PreCPUStats.CPUUsage.TotalUsage;
            double systemDelta = stats.CPUStats.SystemUsage - stats.PreCPUStats.SystemUsage;

            if (systemDelta > 0.0 && cpuDelta > 0.0)
            {
                // 멀티코어를 고려한 계산 (CPU 사용량 / 시스템 사용량 * 코어 수 * 100)
                return (cpuDelta / systemDelta) * stats.CPUStats.OnlineCPUs * 100.0;
            }

            return 0.0;
        }
    }
}