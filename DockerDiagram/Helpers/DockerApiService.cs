using Docker.DotNet;
using Docker.DotNet.Models;
using DockerDiagram.Models;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.Runtime.InteropServices;

namespace DockerDiagram.Helpers
{
    public class DockerApiService : IDockerService
    {
        /*private static readonly Lazy<DockerApiService> _instance = new Lazy<DockerApiService>(() => new DockerApiService());

        public static DockerApiService Instance => _instance.Value;*/

        private readonly DockerClient _client;

        public DockerApiService()
        {
            var dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new Uri("npipe://./pipe/docker_engine") : new Uri("unix:///var/run/docker.sock");
            var config = new DockerClientConfiguration(dockerUri);
            _client = config.CreateClient();
        }

        // 1. 기본 조회 및 연결 확인

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
                    Type = NodeType.Container
                });
            }
            return result;
        }

        // 도커 이미지 목록 조회
        public async Task<List<DockerImage>> GetImagesAsync()
        {
            var images = await _client.Images.ListImagesAsync(new ImagesListParameters { All = false }); // intermediate 이미지 제외
            var result = new List<DockerImage>();
            foreach (var img in images)
            {
                string repoTag = (img.RepoTags != null && img.RepoTags.Count > 0) ? img.RepoTags[0] : "<none>:<none>"; // dangling, untagged 이미지 처리
                var parts = repoTag.Split(':');
                result.Add(new DockerImage
                {
                    Id = img.ID,
                    Repository = parts.Length > 0 ? parts[0] : "<none>",
                    Tag = parts.Length > 1 ? parts[1] : "<none>",
                    Size = img.Size
                });
            }
            return result;
        }

        // 도커 볼륨 목록 조회
        public async Task<List<DockerContainer>> GetVolumesAsync()
        {
            var volumes = await _client.Volumes.ListAsync();
            return volumes.Volumes.Select(v => new DockerContainer
            {
                Name = v.Name,
                Type = NodeType.Volume,
                Image = "local"
            }).ToList();
        }

        // 도커 네트워크 목록 조회
        public async Task<List<DockerContainer>> GetNetworksAsync()
        {
            var networks = await _client.Networks.ListNetworksAsync();
            return networks.Select(n => new DockerContainer
            {
                Name = n.Name,
                Type = NodeType.Network,
                Image = n.Driver,
                Id = n.ID
            }).ToList();
        }

        // 2. 조회

        // 컨테이너 상세 정보 조회 (최신 라이브러리 대응: ContainerInspectResponse 사용. 3.125.15 버전부터 변경됨)
        public async Task<ContainerInspectResponse> InspectContainerAsync(string containerId) // 컨테이너 상세 정보 조회(id 기반)
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

        // 이 볼륨을 사용 중인 컨테이너 목록 찾기 (모든 컨테이너 대조해서 찾기)
        public async Task<List<string>> GetContainersUsingVolumeAsync(string volumeName)
        {
            var containers = await _client.Containers.ListContainersAsync( // 모든 컨테이너 조회
                new ContainersListParameters { All = true });

            var result = new List<string>();

            foreach (var c in containers)
            {
                if (c.Mounts == null) continue;

                // 마운트된 볼륨 이름 또는 소스 경로가 일치하는지 확인
                if (c.Mounts.Any(m => m.Name == volumeName || m.Source.EndsWith(volumeName)))
                {
                    string name = c.Names[0].TrimStart('/');
                    result.Add(name);
                }
            }
            return result;
        }

        // 3. 컨테이너 제어 (Start/Stop/Pause 등)

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
            // 강제 삭제 (Force=true)
            await _client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true, RemoveVolumes = false });
        }

        // 4. 생성 및 관리

        // 이미지 풀링 (도커 허브에서 이미지 다운로드, 단, 사설 저장소는 아직 안됨)
        public async Task PullImageAsync(string image, string tag)
        {
            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = image, Tag = tag },
                null,
                new Progress<JSONMessage>());
        }

        // 컨테이너 생성 및 시작
        public async Task<string> CreateAndStartContainerAsync(
            string name, string image, string tag, List<string> ports, List<string> envs, List<string> volumes,
            string restartPolicy, long memoryMb, double cpuCount)
        {
            string fullImageName = $"{image}:{tag}";

            var portBindings = new Dictionary<string, IList<PortBinding>>();
            var exposedPorts = new Dictionary<string, EmptyStruct>();
            foreach (var p in ports)
            {
                var parts = p.Split(':');
                string hostPort = "";
                string rawContainerPort = "";

                // case 1 : "8080:80" (호스트 포트 지정)
                if (parts.Length == 2)
                {
                    hostPort = parts[0];
                    rawContainerPort = parts[1];
                }
                // case 2 : "80" (호스트 포트 랜덤 할당, 컨테이너만 할당됨. 예 : -p 80)
                else if (parts.Length == 1 && !string.IsNullOrWhiteSpace(parts[0]))
                {
                    hostPort = "0"; // "0"을 주면 도커가 알아서 남는 포트를 할당함
                    rawContainerPort = parts[0];
                }
                else
                {
                    continue; // 잘못된 형식이면 건너뜀
                }

                string containerPort;
                if (rawContainerPort.Contains('/'))
                {
                    containerPort = rawContainerPort; // 예: 53/udp, 80/tcp
                }
                else
                {
                    containerPort = rawContainerPort + "/tcp"; // 예: 80 -> 80/tcp
                }

                exposedPorts[containerPort] = new EmptyStruct();

                // 호스트 포트 바인딩 추가
                portBindings[containerPort] = new List<PortBinding>{new PortBinding { HostPort = hostPort }};
            }

            long memoryBytes = memoryMb > 0 ? memoryMb * 1024 * 1024 : 0;
            long nanoCpus = cpuCount > 0 ? (long)(cpuCount * 1_000_000_000) : 0;

            var parameters = new CreateContainerParameters // 설정한 값들로 컨테이너 파라미터 생성
            {
                Image = fullImageName,
                Name = name,
                Env = envs,
                ExposedPorts = exposedPorts,
                HostConfig = new HostConfig
                {
                    PortBindings = portBindings,
                    Binds = volumes,
                    Memory = memoryBytes,
                    NanoCPUs = nanoCpus,
                    RestartPolicy = new RestartPolicy
                    {
                        Name = (RestartPolicyKind)Enum.Parse(typeof(RestartPolicyKind), restartPolicy, true)
                    }
                }
            };

            var response = await _client.Containers.CreateContainerAsync(parameters); // 생성
            await _client.Containers.StartContainerAsync(response.ID, new ContainerStartParameters()); // 생성 후 id를 기반으로 시작
            return response.ID;
        }

        // 호스트 -> 컨테이너(볼륨 연결로 삭제할 때 백업에 쓰는 용도)
        public async Task CopyFromContainerAsync(string containerId, string containerPath, string hostPath)
        {
            var tarResponse = await _client.Containers.GetArchiveFromContainerAsync(containerId, new GetArchiveFromContainerParameters
            {
                Path = containerPath
            }, false);

            TarFile.ExtractToDirectory(tarResponse.Stream, hostPath, overwriteFiles: true);
        }

        // 호스트 <- 컨테이너(볼륨 연결로 삭제할 때 백업에 쓰는 용도)
        public async Task CopyToContainerAsync(string containerId, string hostPath, string containerPath)
        {
            // 임시 파일 경로 생성
            string tempTarFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tar");

            try
            {
                TarFile.CreateFromDirectory(sourceDirectoryName: hostPath, destinationFileName: tempTarFile, includeBaseDirectory: false);

                // 생성된 Tar 파일을 Docker로 전송
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
                // 임시 파일 삭제
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

        // 5. 삭제

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

        // 6. 터미널 유틸리티

        // Windows CMD를 열고 docker exec 실행
        public void OpenTerminal(string containerId)
        {
            // 앞의 명령이 실패하면(||) 뒤의 명령을 실행하는 방식. 순서: bash -> sh -> cmd -> powershell
            string commands = $"docker exec -it {containerId} /bin/bash || " +
                              $"docker exec -it {containerId} sh || " +
                              $"docker exec -it {containerId} cmd || " +
                              $"docker exec -it {containerId} powershell";

            var processInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/K \"{commands}\"", // 전체를 따옴표로 감싸 안전하게 처리
                UseShellExecute = true
            };
            Process.Start(processInfo);
        }

        // 명령어 실행
        public async Task ExecuteCommandAsync(string containerId, string command)
        {
            try
            {
                // 컨테이너 OS 확인 (Linux인지 Windows인지)
                var inspect = await _client.Containers.InspectContainerAsync(containerId);
                string os = inspect.Platform; // "linux" or "windows"

                // OS에 따른 쉘 명령어 설정
                string[] cmdShell;
                if (os.Contains("windows", StringComparison.OrdinalIgnoreCase))
                {
                    // Windows 컨테이너
                    cmdShell = new[] { "cmd", "/c", command };
                }
                else
                {
                    // Linux 컨테이너 (기본값)
                    cmdShell = new[] { "/bin/sh", "-c", command };
                }

                var execParams = new ContainerExecCreateParameters
                {
                    Cmd = cmdShell,
                    AttachStdout = true,
                    AttachStderr = true,
                };

                var execCreateResp = await _client.Exec.ExecCreateContainerAsync(containerId, execParams);

                // 실행 시작
                await _client.Exec.StartContainerExecAsync(execCreateResp.ID, default);

                // 실행 완료 대기 (Polling)
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
                    await Task.Delay(100); // 0.1초 대기
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DockerDiscovery] ExecuteCommandAsync 에러: {ex.Message}");
            }
        }

        // 7. 네트워크 연결 관리

        // 네트워크 연결 (docker network connect [망] [컨테이너] [--ip 정적아이피]), 사용자 정의 네트워크에서만 동작
        public async Task ConnectNetworkAsync(string networkId, string containerId, string? staticIp = null)
        {
            try
            {
                // 네트워크 연결 설정 객체 생성
                var config = new NetworkConnectParameters
                {
                    Container = containerId
                };

                // 정적 IP(Static IP)가 입력된 경우 설정 추가
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

                // 3. 실제 Docker 데몬에 연결 요청
                await _client.Networks.ConnectNetworkAsync(networkId, config);

                // 성공 로그
                Console.WriteLine($"Network Connected: {networkId} -> {containerId} (IP: {staticIp ?? "Auto"})");
            }
            catch (DockerApiException ex)
            {
                // 이미 연결되어 있거나 IP가 충돌나는 경우(Docker API는 보통 403 Forbidden이나 500 Error를 반환하며 메시지에 이유가 포함됨)
                if (ex.Message.Contains("already exists") || ex.Message.Contains("address already in use"))
                {
                    throw new Exception($"네트워크 연결 실패: 이미 연결되어 있거나 IP({staticIp})가 다른 컨테이너에서 사용 중입니다.");
                }

                // 그 외 알 수 없는 에러는 그대로 던져서 상위(UI)에서 메시지박스를 띄우게 함
                throw;
            }
        }

        // 네트워크 해제 (docker network disconnect [망] [컨테이너])
        public async Task DisconnectNetworkAsync(string networkId, string containerId)
        {
            await _client.Networks.DisconnectNetworkAsync(networkId, new NetworkDisconnectParameters
            {
                Container = containerId,
                Force = true
            });
        }
    }
}