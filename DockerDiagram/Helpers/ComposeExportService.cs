using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using DockerDiagram.Helpers;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DockerDiagram.Helpers
{
    public static class ComposeExportService
    {
        public static void ExportToCompose(SheetViewModel sheet, IDialogService dialogService)
        {
            if (sheet == null || sheet.Nodes.Count == 0)
            {
                dialogService.ShowMessage("내보낼 노드가 없습니다.");
                return;
            }

            var dlg = new SaveFileDialog
            {
                Filter = "Docker Compose File (*.yml)|*.yml|All Files (*.*)|*.*",
                FileName = "docker-compose.yml",
                Title = "Export to Docker Compose"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    string yamlContent = GenerateYaml(sheet);
                    File.WriteAllText(dlg.FileName, yamlContent, Encoding.UTF8);
                    dialogService.ShowInfo($"파일이 생성되었습니다!\n{dlg.FileName}", "성공");
                }
                catch (Exception ex)
                {
                    dialogService.ShowMessage($"내보내기 실패: {ex.Message}");
                }
            }
        }

        private static string GenerateYaml(SheetViewModel sheet)
        {
            // 1. YAML 변환용 최상위 객체 생성
            var composeFile = new ComposeFileModel();

            var nodeIdToServiceName = new Dictionary<string, string>();
            var usedServiceNames = new HashSet<string>();
            var containerNodes = sheet.Nodes.Where(n => n.Type == NodeType.Container).ToList();

            // 서비스 이름 확정 (중복 방지 매핑)
            foreach (var node in containerNodes)
            {
                string baseName = SanitizeServiceName(node.Name);
                string uniqueName = EnsureUniqueName(baseName, usedServiceNames);

                usedServiceNames.Add(uniqueName);
                nodeIdToServiceName[node.Id] = uniqueName;
            }

            // 2. Services 생성
            foreach (var node in containerNodes)
            {
                string serviceName = nodeIdToServiceName[node.Id];
                var service = new ComposeService
                {
                    ContainerName = SanitizeContainerName(node.Name),
                    Image = !string.IsNullOrWhiteSpace(node.ImageName) ? node.ImageName : "nginx:latest"
                };

                if (!string.IsNullOrEmpty(node.RestartPolicy) && node.RestartPolicy != "no")
                    service.Restart = node.RestartPolicy;

                if (node.PortBindings != null && node.PortBindings.Count > 0)
                    service.Ports = new List<string>(node.PortBindings);

                if (node.EnvironmentVariables != null && node.EnvironmentVariables.Count > 0)
                    service.Environment = new List<string>(node.EnvironmentVariables);

                var connectedVolumes = GetConnectedVolumes(node, sheet);
                if (connectedVolumes.Count > 0)
                    service.Volumes = connectedVolumes;

                // 의존성(depends_on)
                var depConns = sheet.Connectors
                    .Where(c => c.Source == node && c.RelationType == RelationType.Dependency &&
                                c.Target is NodeViewModel tNode && tNode.Type == NodeType.Container)
                    .ToList();

                if (depConns.Count > 0)
                {
                    service.DependsOn = new List<string>();
                    foreach (var conn in depConns)
                    {
                        if (nodeIdToServiceName.TryGetValue(conn.Target.Id, out string? depServiceName))
                            service.DependsOn.Add(depServiceName);
                    }
                }

                // 네트워크 정보 조회
                var connectedNets = GetConnectedNetworks(node, sheet);
                if (connectedNets.Count > 0)
                {
                    // 정적 IP가 하나라도 있으면 Dictionary 방식, 없으면 List 방식 사용
                    bool hasStaticIp = connectedNets.Any(n => !string.IsNullOrEmpty(n.IpAddress) && IsValidIp(n.IpAddress));

                    if (hasStaticIp)
                    {
                        var netDict = new Dictionary<string, ComposeServiceNetwork>();
                        foreach (var net in connectedNets)
                        {
                            var netConfig = new ComposeServiceNetwork();
                            if (!string.IsNullOrEmpty(net.IpAddress) && IsValidIp(net.IpAddress))
                                netConfig.Ipv4Address = net.IpAddress;
                            netDict[net.NetworkName!] = netConfig;
                        }
                        service.Networks = netDict;
                    }
                    else
                    {
                        service.Networks = connectedNets.Select(n => n.NetworkName!).ToList();
                    }
                }

                composeFile.Services[serviceName] = service;
            }

            // 3. Networks 생성
            var networkGroups = sheet.Groups.Where(g => g.Type == GroupType.Network).ToList();
            if (networkGroups.Any())
            {
                composeFile.Networks = new Dictionary<string, ComposeNetwork>();
                foreach (var netGroup in networkGroups)
                {
                    var netObj = new ComposeNetwork { Driver = "bridge" };

                    string? generatedSubnet = GetSubnetIfRequired(netGroup.Title, containerNodes);
                    if (!string.IsNullOrEmpty(generatedSubnet))
                    {
                        netObj.Ipam = new ComposeIpam
                        {
                            Config = new List<ComposeIpamConfig> { new ComposeIpamConfig { Subnet = generatedSubnet } }
                        };
                    }
                    composeFile.Networks[netGroup.Title] = netObj;
                }
            }

            // 4. Volumes 생성
            var volumes = sheet.Nodes.Where(n => n.Type == NodeType.Volume).ToList();
            var namedVolumes = volumes.Where(vol =>
                !(vol.Name.StartsWith("/") || vol.Name.StartsWith("./") || vol.Name.StartsWith("../") ||
                  vol.Name.StartsWith("~/") || Regex.IsMatch(vol.Name, @"^[a-zA-Z]:[\\/]"))).ToList();

            if (namedVolumes.Any())
            {
                composeFile.Volumes = new Dictionary<string, object>();
                foreach (var vol in namedVolumes)
                {
                    composeFile.Volumes[vol.Name] = new object(); // 빈 객체 {} 생성
                }
            }

            // 5. YamlDotNet을 이용해 C# 객체를 YAML 문자열로 직렬화(Serialize)
            var serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance) // ContainerName -> container_name 자동 변환
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections) // 비어있는 리스트나 null은 출력 안함
                .Build();

            return serializer.Serialize(composeFile);
        }

        // --- 헬퍼 메서드들 ---
        private static string SanitizeServiceName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return "service";
            string s = rawName.ToLowerInvariant();
            s = Regex.Replace(s, "[^a-z0-9_]", "_");
            s = s.Trim('_');
            return string.IsNullOrEmpty(s) ? "service" : s;
        }

        private static string SanitizeContainerName(string rawName) =>
            string.IsNullOrWhiteSpace(rawName) ? "container" : Regex.Replace(rawName, "[^a-zA-Z0-9_.-]", "");

        private static string EnsureUniqueName(string name, HashSet<string> usedNames)
        {
            if (!usedNames.Contains(name)) return name;
            int count = 1;
            while (usedNames.Contains($"{name}_{count}")) count++;
            return $"{name}_{count}";
        }

        private static bool IsValidIp(string ip) => !string.IsNullOrWhiteSpace(ip) && ip.Count(c => c == '.') == 3;

        private static List<string> GetConnectedVolumes(NodeViewModel container, SheetViewModel sheet)
        {
            var list = new List<string>();
            var conns = sheet.Connectors
                .Where(c => (c.Source == container && c.Target is NodeViewModel tNode1 && tNode1.Type == NodeType.Volume) ||
                            (c.Target == container && c.Source is NodeViewModel sNode2 && sNode2.Type == NodeType.Volume))
                .ToList();

            foreach (var c in conns)
            {
                var volNode = c.Source == container ? (NodeViewModel)c.Target : (NodeViewModel)c.Source;
                string mountPath = !string.IsNullOrEmpty(c.MountPath) ? c.MountPath : "/data";

                string hostPath = volNode.Name;

                // =================================================================
                // ★ [환경 변수 적용부] 호스트 경로를 환경 변수 포맷으로 가공합니다.
                // =================================================================
                // 1. 특수문자를 언더바(_)로 치환하여 안전한 환경 변수명 생성
                string safeEnvName = Regex.Replace(hostPath, @"[^a-zA-Z0-9_]", "_").ToUpperInvariant();

                // 2. 도커 컴포즈 '기본값(Fallback)' 문법 적용 (${변수명:-기본경로}:컨테이너경로)
                string volumeMapping = $"${{HOST_VOL_{safeEnvName}:-{hostPath}}}:{mountPath}";

                list.Add(volumeMapping);
            }
            return list;
        }

        private class NetworkInfo { public string? NetworkName; public string? IpAddress; }

        private static List<NetworkInfo> GetConnectedNetworks(NodeViewModel container, SheetViewModel sheet)
        {
            var list = new List<NetworkInfo>();
            var networkGroups = sheet.Groups.Where(g => g.Type == GroupType.Network);

            foreach (var group in networkGroups)
            {
                if (group.ContainedNodes.Contains(container))
                {
                    string? ip = null;
                    if (container.NetworkIpMap != null && container.NetworkIpMap.ContainsKey(group.Title))
                        ip = container.NetworkIpMap[group.Title];

                    list.Add(new NetworkInfo { NetworkName = group.Title, IpAddress = ip });
                }
            }
            return list;
        }

        private static string? GetSubnetIfRequired(string networkName, List<NodeViewModel> containers)
        {
            foreach (var container in containers)
            {
                if (container.NetworkIpMap != null &&
                    container.NetworkIpMap.TryGetValue(networkName, out string ip) &&
                    IsValidIp(ip))
                {
                    var parts = ip.Split('.');
                    if (parts.Length == 4) return $"{parts[0]}.{parts[1]}.{parts[2]}.0/24";
                }
            }
            return null;
        }
    }
}