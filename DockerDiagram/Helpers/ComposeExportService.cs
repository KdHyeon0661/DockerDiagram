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
    /// <summary>
    /// 현재 화면(Sheet)에 그려진 도커 다이어그램을 분석하여, 
    /// 실제 실행 가능한 docker-compose.yml 파일로 내보내는(Export) 정적 서비스 클래스입니다.
    /// </summary>
    public static class ComposeExportService
    {
        /// <summary>
        /// 사용자에게 저장 위치를 묻는 다이얼로그를 띄우고, 다이어그램 데이터를 YAML 형식의 파일로 저장합니다.
        /// </summary>
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
                    string yamlContent = GenerateYaml(sheet); // YAML 문자열 생성
                    File.WriteAllText(dlg.FileName, yamlContent, Encoding.UTF8);
                    dialogService.ShowInfo($"파일이 생성되었습니다!\n{dlg.FileName}", "성공");
                }
                catch (Exception ex)
                {
                    dialogService.ShowMessage($"내보내기 실패: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 시트의 데이터(노드, 연결선, 그룹)를 분석하여 ComposeFileModel 객체로 매핑한 뒤, YAML 문자열로 직렬화합니다.
        /// </summary>
        private static string GenerateYaml(SheetViewModel sheet)
        {
            // 1. import 원본이 있으면 그 구조를 바탕으로 시작해, 아직 UI가 직접 편집하지 않는 Compose 필드를 보존합니다.
            var composeRoot = ComposeYamlHelper.ToMutableRootMap(sheet.ComposeRawYaml);

            var nodeIdToServiceName = new Dictionary<string, string>();
            var usedServiceNames = new HashSet<string>();
            var containerNodes = sheet.Nodes.Where(n => n.Type == NodeType.Container).ToList();

            // 서비스 이름 확정 (중복 방지 매핑)
            foreach (var node in containerNodes)
            {
                string baseName = !string.IsNullOrWhiteSpace(node.ComposeServiceName)
                    ? SanitizeServiceName(node.ComposeServiceName)
                    : SanitizeServiceName(node.Name);
                string uniqueName = EnsureUniqueName(baseName, usedServiceNames);

                usedServiceNames.Add(uniqueName);
                nodeIdToServiceName[node.Id] = uniqueName;
            }

            // 2. Services 생성
            var services = new Dictionary<object, object>();
            foreach (var node in containerNodes)
            {
                string serviceName = nodeIdToServiceName[node.Id];
                var service = ComposeYamlHelper.ToMutableServiceMap(node.ComposeRawServiceYaml);

                ComposeYamlHelper.SetValue(service, "container_name", SanitizeContainerName(node.Name));

                bool hasBuild = ComposeYamlHelper.HasKey(service, "build");
                bool hasImage = ComposeYamlHelper.HasKey(service, "image");
                bool imageLooksSynthetic = string.IsNullOrWhiteSpace(node.ImageName)
                    || node.ImageName == "unknown-image"
                    || node.ImageName.StartsWith("build:", StringComparison.OrdinalIgnoreCase);

                if (!imageLooksSynthetic)
                    ComposeYamlHelper.SetValue(service, "image", node.ImageName);
                else if (!hasBuild && !hasImage)
                    ComposeYamlHelper.SetValue(service, "image", "nginx:latest");

                if (!ComposeYamlHelper.HasKey(service, "restart") &&
                    !string.IsNullOrEmpty(node.RestartPolicy) &&
                    node.RestartPolicy != "no")
                    ComposeYamlHelper.SetValue(service, "restart", node.RestartPolicy);

                if (!ComposeYamlHelper.HasKey(service, "ports") && node.PortBindings != null && node.PortBindings.Count > 0)
                    ComposeYamlHelper.SetValue(service, "ports", new List<string>(node.PortBindings));

                if (!ComposeYamlHelper.HasKey(service, "environment") && node.EnvironmentVariables != null && node.EnvironmentVariables.Count > 0)
                    ComposeYamlHelper.SetValue(service, "environment", new List<string>(node.EnvironmentVariables));

                var connectedVolumes = GetConnectedVolumes(node, sheet);
                if (!ComposeYamlHelper.HasKey(service, "volumes") && connectedVolumes.Count > 0)
                    ComposeYamlHelper.SetValue(service, "volumes", connectedVolumes);

                // 의존성(depends_on) 정보 세팅
                var depConns = sheet.Connectors
                    .Where(c => c.Source == node && c.RelationType == RelationType.Dependency &&
                                c.Target is NodeViewModel tNode && tNode.Type == NodeType.Container)
                    .ToList();

                if (!ComposeYamlHelper.HasKey(service, "depends_on") && depConns.Count > 0)
                {
                    var dependsOn = new List<string>();
                    foreach (var conn in depConns)
                    {
                        if (nodeIdToServiceName.TryGetValue(conn.Target.Id, out string? depServiceName))
                            dependsOn.Add(depServiceName);
                    }
                    ComposeYamlHelper.SetValue(service, "depends_on", dependsOn);
                }

                // 네트워크 정보 조회 및 세팅
                var connectedNets = GetConnectedNetworks(node, sheet);
                if (!ComposeYamlHelper.HasKey(service, "networks") && connectedNets.Count > 0)
                {
                    ComposeYamlHelper.SetValue(service, "networks", BuildServiceNetworks(connectedNets));
                }

                services[serviceName] = service;
            }

            composeRoot["services"] = services;

            // 3. Networks 생성. 원본 네트워크 설정(driver/options/ipam/labels 등)은 보존하고, 새 그룹만 추가합니다.
            var networkGroups = sheet.Groups.Where(g => g.Type == GroupType.Network).ToList();
            if (networkGroups.Any())
            {
                var networks = ComposeYamlHelper.GetMapping(ComposeYamlHelper.GetValue(composeRoot, "networks"))
                    ?? new Dictionary<object, object>();

                foreach (var netGroup in networkGroups)
                {
                    if (ComposeYamlHelper.HasKey(networks, netGroup.Title)) continue;

                    var netObj = new Dictionary<object, object> { ["driver"] = "bridge" };

                    string? generatedSubnet = GetSubnetIfRequired(netGroup.Title, containerNodes);
                    if (!string.IsNullOrEmpty(generatedSubnet))
                    {
                        netObj["ipam"] = new Dictionary<object, object>
                        {
                            ["config"] = new List<object>
                            {
                                new Dictionary<object, object> { ["subnet"] = generatedSubnet }
                            }
                        };
                    }
                    networks[netGroup.Title] = netObj;
                }

                composeRoot["networks"] = networks;
            }

            // 4. Volumes 생성. 원본 볼륨 설정(driver/driver_opts/external/labels 등)은 보존합니다.
            var volumes = sheet.Nodes.Where(n => n.Type == NodeType.Volume).ToList();
            var namedVolumes = volumes.Where(vol =>
                !(vol.Name.StartsWith("/") || vol.Name.StartsWith("./") || vol.Name.StartsWith("../") ||
                  vol.Name.StartsWith("~/") || Regex.IsMatch(vol.Name, @"^[a-zA-Z]:[\\/]"))).ToList();

            if (namedVolumes.Any())
            {
                var volumeMap = ComposeYamlHelper.GetMapping(ComposeYamlHelper.GetValue(composeRoot, "volumes"))
                    ?? new Dictionary<object, object>();

                foreach (var vol in namedVolumes)
                {
                    if (!ComposeYamlHelper.HasKey(volumeMap, vol.Name))
                        volumeMap[vol.Name] = new Dictionary<object, object>(); // 빈 객체 {} 생성
                }
                composeRoot["volumes"] = volumeMap;
            }

            // 5. YamlDotNet을 이용해 C# 객체를 YAML 문자열로 직렬화(Serialize)
            var serializer = new SerializerBuilder()
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections) // 비어있는 리스트나 null은 출력 안함
                .Build();

            return serializer.Serialize(composeRoot);
        }

        // --- 헬퍼 메서드들 ---

        /// <summary>
        /// 도커 컴포즈의 서비스 이름 규칙에 맞게 특수문자를 언더바(_)로 치환합니다.
        /// </summary>
        private static string SanitizeServiceName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return "service";
            string s = rawName.ToLowerInvariant();
            s = Regex.Replace(s, "[^a-z0-9_]", "_");
            s = s.Trim('_');
            return string.IsNullOrEmpty(s) ? "service" : s;
        }

        /// <summary>
        /// 도커 컨테이너 이름 규칙에 맞게 사용할 수 없는 문자를 제거합니다.
        /// </summary>
        private static string SanitizeContainerName(string rawName) =>
            string.IsNullOrWhiteSpace(rawName) ? "container" : Regex.Replace(rawName, "[^a-zA-Z0-9_.-]", "");

        /// <summary>
        /// 중복된 서비스 이름이 있을 경우 뒤에 숫자를 붙여 고유한 이름으로 만듭니다.
        /// </summary>
        private static string EnsureUniqueName(string name, HashSet<string> usedNames)
        {
            if (!usedNames.Contains(name)) return name;
            int count = 1;
            while (usedNames.Contains($"{name}_{count}")) count++;
            return $"{name}_{count}";
        }

        /// <summary>
        /// 주어진 문자열이 유효한 형태의 IPv4 주소인지 검사합니다.
        /// </summary>
        private static bool IsValidIp(string? ip) => !string.IsNullOrWhiteSpace(ip) && ip.Count(c => c == '.') == 3;

        /// <summary>
        /// 특정 컨테이너와 연결된 볼륨 목록을 찾고, .env 파일과 연동할 수 있는 환경변수 포맷의 문자열 리스트로 반환합니다.
        /// </summary>
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

                // 1. 특수문자를 언더바(_)로 치환하여 안전한 환경 변수명 생성
                string safeEnvName = Regex.Replace(hostPath, @"[^a-zA-Z0-9_]", "_").ToUpperInvariant();

                // 2. 도커 컴포즈 '기본값(Fallback)' 문법 적용 (${변수명:-기본경로}:컨테이너경로)
                string volumeMapping = $"${{HOST_VOL_{safeEnvName}:-{hostPath}}}:{mountPath}";

                list.Add(volumeMapping);
            }
            return list;
        }

        private static object BuildServiceNetworks(List<NetworkInfo> connectedNets)
        {
            bool hasStaticIp = connectedNets.Any(n => !string.IsNullOrEmpty(n.IpAddress) && IsValidIp(n.IpAddress));
            if (!hasStaticIp)
                return connectedNets.Select(n => n.NetworkName!).ToList();

            var netDict = new Dictionary<object, object>();
            foreach (var net in connectedNets)
            {
                var netConfig = new Dictionary<object, object>();
                if (!string.IsNullOrEmpty(net.IpAddress) && IsValidIp(net.IpAddress))
                    netConfig["ipv4_address"] = net.IpAddress;

                netDict[net.NetworkName!] = netConfig;
            }
            return netDict;
        }

        private class NetworkInfo { public string? NetworkName; public string? IpAddress; }

        /// <summary>
        /// 특정 컨테이너가 속해있는 네트워크 그룹 목록과 각각의 할당된 IP를 찾아 반환합니다.
        /// </summary>
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

        /// <summary>
        /// 특정 네트워크 안에 정적 IP를 가진 컨테이너가 있다면, 해당 IP를 분석하여 자동으로 서브넷(Subnet) 대역을 계산해 반환합니다.
        /// </summary>
        private static string? GetSubnetIfRequired(string networkName, List<NodeViewModel> containers)
        {
            foreach (var container in containers)
            {
                if (container.NetworkIpMap != null &&
                    container.NetworkIpMap.TryGetValue(networkName, out var ip) &&
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
