using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerDiagram.Models;
using ICSharpCode.SharpZipLib.Tar;
using System.Text;

namespace DockerDiagram.Helpers
{
    public class DockerApiService
    {
        private readonly DockerClient _client;

        public DockerApiService()
        {
            // 윈도우 기본 Named Pipe 연결
            _client = new DockerClientConfiguration(new Uri("npipe://./pipe/docker_engine"))
                .CreateClient();
        }

        // --- 1. 기본 조회 및 연결 확인 ---

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
                    foreach (var p in c.Ports)
                    {
                        if (p.PublicPort > 0)
                            ports.Add($"{p.PublicPort}:{p.PrivatePort}");
                    }
                    portStr = string.Join(", ", ports);
                }

                result.Add(new DockerContainer
                {
                    Id = c.ID.Substring(0, 12),
                    Name = c.Names[0].TrimStart('/'),
                    Image = c.Image,
                    State = c.State,
                    Ports = portStr,
                    Type = NodeType.Container
                });
            }
            return result;
        }

        public async Task<List<DockerImage>> GetImagesAsync()
        {
            var images = await _client.Images.ListImagesAsync(new ImagesListParameters { All = false });
            var result = new List<DockerImage>();
            foreach (var img in images)
            {
                string repoTag = (img.RepoTags != null && img.RepoTags.Count > 0) ? img.RepoTags[0] : "<none>:<none>";
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

        // --- 2. 상세 조회 (Inspect) ---

        // 컨테이너 상세 정보 조회 (최신 라이브러리 대응: ContainerInspectResponse 사용. 3.125.15 버전부터 변경됨)
        public async Task<ContainerInspectResponse> InspectContainerAsync(string containerId)
        {
            return await _client.Containers.InspectContainerAsync(containerId);
        }

        // 볼륨 상세 정보 조회
        public async Task<VolumeResponse> InspectVolumeAsync(string name)
        {
            return await _client.Volumes.InspectAsync(name);
        }

        // 이 볼륨을 사용 중인 컨테이너 목록 찾기 (전수 조사)
        public async Task<List<string>> GetContainersUsingVolumeAsync(string volumeName)
        {
            var containers = await _client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true });

            var result = new List<string>();

            foreach (var c in containers)
            {
                if (c.Mounts == null) continue;

                // 마운트 정보 중 Name이 일치하거나 Source 경로가 해당 볼륨으로 끝나는 경우
                if (c.Mounts.Any(m => m.Name == volumeName || m.Source.EndsWith(volumeName)))
                {
                    string name = c.Names[0].TrimStart('/');
                    result.Add(name);
                }
            }
            return result;
        }

        // --- 3. 컨테이너 제어 (Start/Stop/Pause 등) ---

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
            // 강제 삭제 (Force=true)
            await _client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true, RemoveVolumes = false });
        }

        // --- 4. 생성 및 관리 ---

        public async Task PullImageAsync(string image, string tag)
        {
            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = image, Tag = tag },
                null,
                new Progress<JSONMessage>());
        }

        public async Task DeleteImageAsync(string imageId, bool force = false)
        {
            await _client.Images.DeleteImageAsync(imageId, new ImageDeleteParameters { Force = force });
        }

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
                if (parts.Length == 2)
                {
                    string hostPort = parts[0];
                    string containerPort = parts[1] + "/tcp";
                    exposedPorts[containerPort] = new EmptyStruct();
                    portBindings[containerPort] = new List<PortBinding> { new PortBinding { HostPort = hostPort } };
                }
            }

            long memoryBytes = memoryMb > 0 ? memoryMb * 1024 * 1024 : 0;
            long nanoCpus = cpuCount > 0 ? (long)(cpuCount * 1_000_000_000) : 0;

            var parameters = new CreateContainerParameters
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

            var response = await _client.Containers.CreateContainerAsync(parameters);
            await _client.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());
            return response.ID;
        }

        // --- 5. 볼륨 및 네트워크 생성/목록 ---

        public async Task CreateVolumeAsync(string name, string driver)
        {
            await _client.Volumes.CreateAsync(new VolumesCreateParameters
            {
                Name = name,
                Driver = driver
            });
        }

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

        // --- 6. 터미널 유틸리티 ---

        public void OpenTerminal(string containerId)
        {
            // Windows CMD를 열고 docker exec 실행
            var processInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/K docker exec -it {containerId} /bin/bash || docker exec -it {containerId} sh",
                UseShellExecute = true
            };
            Process.Start(processInfo);
        }

        public async Task<NetworkResponse> InspectNetworkAsync(string id)
        {
            return await _client.Networks.InspectNetworkAsync(id);
        }

        // 네트워크 삭제
        public async Task RemoveNetworkAsync(string id)
        {
            await _client.Networks.DeleteNetworkAsync(id);
        }

        public async Task RemoveVolumeAsync(string name)
        {
            // 볼륨 삭제 API 호출
            await _client.Volumes.RemoveAsync(name, false);
        }

        public async Task CopyFromContainerAsync(string containerId, string containerPath, string hostPath)
        {
            var tarResponse = await _client.Containers.GetArchiveFromContainerAsync(containerId, new GetArchiveFromContainerParameters
            {
                Path = containerPath
            }, false);

            // Response 자체 Dispose 제거
            using (var tarInputStream = new TarInputStream(tarResponse.Stream, Encoding.UTF8))
            {
                TarEntry entry;
                while ((entry = tarInputStream.GetNextEntry()) != null)
                {
                    if (entry.IsDirectory) continue;

                    string targetFile = Path.Combine(hostPath, entry.Name);
                    string? dir = Path.GetDirectoryName(targetFile);
                    if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    using (var fileStream = File.Create(targetFile))
                    {
                        tarInputStream.CopyEntryContents(fileStream);
                    }
                }
            }
        }

        // [SharpZipLib 사용] 2. 호스트 -> 컨테이너 (압축 하기)
        public async Task CopyToContainerAsync(string containerId, string hostPath, string containerPath)
        {
            string tempTarFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tar");

            using (var fs = File.Create(tempTarFile))
            using (var tarOutput = new TarOutputStream(fs, Encoding.UTF8))
            {
                AddDirectoryToTar(tarOutput, hostPath, "");
            }

            using (var fs = File.OpenRead(tempTarFile))
            {
                await _client.Containers.ExtractArchiveToContainerAsync(containerId, new ContainerPathStatParameters
                {
                    Path = containerPath,
                    AllowOverwriteDirWithFile = true
                }, fs);
            }

            if (File.Exists(tempTarFile)) File.Delete(tempTarFile);
        }

        private void AddDirectoryToTar(TarOutputStream tarOut, string sourceDir, string currentDirInTar)
        {
            string[] files = Directory.GetFiles(sourceDir);
            foreach (var file in files)
            {
                string fileName = Path.GetFileName(file);
                string entryName = string.IsNullOrEmpty(currentDirInTar) ? fileName : currentDirInTar + "/" + fileName;

                TarEntry entry = TarEntry.CreateTarEntry(entryName);

                using (var fs = File.OpenRead(file))
                {
                    entry.Size = fs.Length;
                    tarOut.PutNextEntry(entry);
                    fs.CopyTo(tarOut);
                }
                tarOut.CloseEntry();
            }

            string[] subDirs = Directory.GetDirectories(sourceDir);
            foreach (var dir in subDirs)
            {
                string dirName = Path.GetFileName(dir);
                string newCurrentDir = string.IsNullOrEmpty(currentDirInTar) ? dirName : currentDirInTar + "/" + dirName;
                AddDirectoryToTar(tarOut, dir, newCurrentDir);
            }
        }

        public async Task ExecuteCommandAsync(string containerId, string command)
        {
            try
            {
                var execCreateResp = await _client.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
                {
                    Cmd = new[] { "/bin/sh", "-c", command },
                    AttachStdout = true,
                    AttachStderr = true,
                    User = "root"
                });

                await _client.Exec.StartContainerExecAsync(execCreateResp.ID, default);
            }
            catch { /* 지금은 무시하자 */ }
        }

        public async Task ConnectNetworkAsync(string networkId, string containerId, string? staticIp = null)
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
        }

        // 2. 네트워크 해제 (docker network disconnect [망] [컨테이너])
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