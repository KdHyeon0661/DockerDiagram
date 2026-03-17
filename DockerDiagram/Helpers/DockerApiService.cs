using Docker.DotNet;
using Docker.DotNet.Models;
using DockerDiagram.Models;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DockerDiagram.Helpers
{
    public class DockerApiService : IDockerService
    {
        private readonly DockerClient _client;
        private bool _disposedValue; // 중복 해제 방지 플래그

        public DockerApiService()
        {
            var dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new Uri("npipe://./pipe/docker_engine") : new Uri("unix:///var/run/docker.sock");
            var config = new DockerClientConfiguration(dockerUri);
            _client = config.CreateClient();
        }

        // =========================================================
        // 1. ISystemService 구현
        // =========================================================

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

        public async Task<List<DockerContainer>> GetContainersAsync()
        {
            var containers = await _client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true }
            );

            var result = new List<DockerContainer>();
            foreach (var c in containers)
            {
                string portStr = "";
                if (c.Ports != null)
                {
                    var ports = new List<string>();
                    foreach (var p in c.Ports ?? Enumerable.Empty<Docker.DotNet.Models.Port>())
                    {
                        var proto = string.IsNullOrWhiteSpace(p.Type) ? "tcp" : p.Type;

                        if (p.PublicPort > 0)
                        {
                            var ipPrefix = string.IsNullOrWhiteSpace(p.IP) ? "" : $"{p.IP}:";
                            ports.Add($"{ipPrefix}{p.PublicPort}->{p.PrivatePort}/{proto}");
                        }
                        else
                        {
                            ports.Add($"{p.PrivatePort}/{proto}");
                        }
                    }
                    portStr = string.Join(", ", ports);
                }

                result.Add(new DockerContainer
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

        public async Task<List<DockerImage>> GetImagesAsync()
        {
            var images = await _client.Images.ListImagesAsync(new ImagesListParameters { All = false });
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

        public async Task<List<DockerVolume>> GetVolumesAsync()
        {
            var volumes = await _client.Volumes.ListAsync();
            return volumes.Volumes.Select(v => new DockerVolume
            {
                Name = v.Name,
                Id = v.Name,
            }).ToList();
        }

        // =========================================================
        // 5. INetworkService 구현
        // =========================================================

        public async Task<List<DockerGroup>> GetNetworksAsync()
        {
            var networks = await _client.Networks.ListNetworksAsync();
            return networks.Select(n => new DockerGroup
            {
                Name = n.Name,
                Id = n.ID,
                Type = GroupType.Network,
                Driver = n.Driver
            }).ToList();
        }

        // ---------------------------------------------------------
        // 상세 기능 구현
        // ---------------------------------------------------------

        public async Task<ContainerInspectResponse> InspectContainerAsync(string containerId)
        {
            return await _client.Containers.InspectContainerAsync(containerId);
        }

        public async Task<VolumeResponse> InspectVolumeAsync(string name)
        {
            return await _client.Volumes.InspectAsync(name);
        }

        public async Task<NetworkResponse> InspectNetworkAsync(string networkId)
        {
            return await _client.Networks.InspectNetworkAsync(networkId);
        }

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

        public async Task StartContainerAsync(string id)
            => await _client.Containers.StartContainerAsync(id, new ContainerStartParameters());

        public async Task StopContainerAsync(string id)
            => await _client.Containers.StopContainerAsync(id, new ContainerStopParameters { WaitBeforeKillSeconds = 5 });

        public async Task PauseContainerAsync(string id)
            => await _client.Containers.PauseContainerAsync(id);

        public async Task UnpauseContainerAsync(string id)
            => await _client.Containers.UnpauseContainerAsync(id);

        public async Task RestartContainerAsync(string id)
            => await _client.Containers.RestartContainerAsync(id, new ContainerRestartParameters());

        public async Task RemoveContainerAsync(string id)
        {
            await _client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true, RemoveVolumes = false });
        }

        public async Task PullImageAsync(string image, string tag)
        {
            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = image, Tag = tag },
                null,
                new Progress<JSONMessage>());
        }

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

        public async Task CopyFromContainerAsync(string containerId, string containerPath, string hostPath)
        {
            var tarResponse = await _client.Containers.GetArchiveFromContainerAsync(containerId, new GetArchiveFromContainerParameters
            {
                Path = containerPath
            }, false);

            TarFile.ExtractToDirectory(tarResponse.Stream, hostPath, overwriteFiles: true);
        }

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

        public async Task CreateVolumeAsync(string name, string driver)
        {
            await _client.Volumes.CreateAsync(new VolumesCreateParameters
            {
                Name = name,
                Driver = driver
            });
        }

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

        public async Task DeleteImageAsync(string imageId, bool force = false)
        {
            await _client.Images.DeleteImageAsync(imageId, new ImageDeleteParameters { Force = force });
        }

        public async Task RemoveVolumeAsync(string name)
        {
            await _client.Volumes.RemoveAsync(name, false);
        }

        public async Task RemoveNetworkAsync(string id)
        {
            await _client.Networks.DeleteNetworkAsync(id);
        }

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

        public async Task ExecuteCommandAsync(string containerId, string command)
        {
            try
            {
                var inspect = await _client.Containers.InspectContainerAsync(containerId);
                string[] cmdShell = inspect.Platform.Contains("windows", StringComparison.OrdinalIgnoreCase)
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

                using (var stream = await _client.Exec.StartAndAttachContainerExecAsync(execCreateResp.ID, false))
                {
                    using var stdoutMs = new MemoryStream();
                    using var stderrMs = new MemoryStream();

                    await stream.CopyOutputToAsync(default, stdoutMs, stderrMs, CancellationToken.None);

                    stdoutMs.Position = 0;
                    stderrMs.Position = 0;

                    string output = Encoding.UTF8.GetString(stdoutMs.ToArray());
                    string error = Encoding.UTF8.GetString(stderrMs.ToArray());

                    if (!string.IsNullOrWhiteSpace(output))
                        Debug.WriteLine($"[DockerDiscovery] Exec Output:\n{output}");

                    if (!string.IsNullOrWhiteSpace(error))
                        Debug.WriteLine($"[DockerDiscovery] Exec Error:\n{error}");
                }

                var finalStatus = await _client.Exec.InspectContainerExecAsync(execCreateResp.ID);
                if (finalStatus.ExitCode != 0)
                {
                    Debug.WriteLine($"[DockerDiscovery] Exec Failed (Code: {finalStatus.ExitCode})");
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

                Debug.WriteLine($"[DockerDiscovery] Network Connected: {networkId} -> {containerId} (IP: {staticIp ?? "Auto"})");
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
                var statsParams = new ContainerStatsParameters { Stream = false };

                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    ContainerStatsResponse? stats = null;
                    var progress = new Progress<ContainerStatsResponse>(r => stats = r);

                    await _client.Containers.GetContainerStatsAsync(containerId, statsParams, progress, cts.Token);

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
                Debug.WriteLine($"[DockerDiscovery] Stats Error: {ex.Message}");
            }

            return new ContainerStats();
        }

        private double CalculateCpuPercentage(ContainerStatsResponse stats)
        {
            double cpuDelta = stats.CPUStats.CPUUsage.TotalUsage - stats.PreCPUStats.CPUUsage.TotalUsage;
            double systemDelta = stats.CPUStats.SystemUsage - stats.PreCPUStats.SystemUsage;

            if (systemDelta > 0.0 && cpuDelta > 0.0)
            {
                return (cpuDelta / systemDelta) * stats.CPUStats.OnlineCPUs * 100.0;
            }

            return 0.0;
        }

        // =========================================================
        // ★ [필수] IDisposable 패턴 구현 (사라졌던 부분)
        // =========================================================
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _client?.Dispose();
                }
                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        public async Task<string> GetContainerLogsAsync(string containerId, int tailCount = 500)
        {
            try
            {
                // 로그 요청 파라미터 (최근 N줄, 타임스탬프 포함, 표준 입출력/에러 모두 가져옴)
                var parameters = new ContainerLogsParameters
                {
                    ShowStdout = true,
                    ShowStderr = true,
                    Tail = tailCount.ToString(),
                    Timestamps = true
                };

                // 도커 클라이언트에서 로그 스트림 받아오기
                using (var stream = await _client.Containers.GetContainerLogsAsync(containerId, false, parameters))
                {
                    using (var stdoutMs = new MemoryStream())
                    using (var stderrMs = new MemoryStream())
                    {
                        // 도커 로그는 특수 헤더가 붙어있어 CopyOutputToAsync로 분리해서 읽어야 함
                        await stream.CopyOutputToAsync(default, stdoutMs, stderrMs, CancellationToken.None);

                        stdoutMs.Position = 0;
                        stderrMs.Position = 0;

                        string stdout = Encoding.UTF8.GetString(stdoutMs.ToArray());
                        string stderr = Encoding.UTF8.GetString(stderrMs.ToArray());

                        // 일반 출력(stdout)과 에러 출력(stderr)을 합쳐서 반환
                        return stdout + stderr;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DockerDiscovery] GetContainerLogsAsync Error: {ex.Message}");
                return $"로그를 가져오는 중 오류가 발생했습니다:\n{ex.Message}";
            }
        }
    }
}