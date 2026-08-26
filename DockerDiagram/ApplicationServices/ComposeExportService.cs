using DockerDiagram.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DockerDiagram.ApplicationServices
{
    /// <summary>
    /// 현재 시트의 Docker 구성을 Compose YAML로 내보냅니다.
    /// </summary>
    public static class ComposeExportService
    {
        /// <summary>
        /// 저장 위치를 선택하고 시트 데이터를 Compose YAML 파일로 저장합니다.
        /// </summary>
        public static void ExportToCompose(SheetViewModel sheet, IDialogService dialogService)
        {
            if (sheet == null || sheet.Nodes.Count == 0)
            {
                dialogService.ShowMessage("내보낼 노드가 없습니다.");
                return;
            }

            string? filePath = dialogService.ShowSaveFileDialog(
                "Docker Compose File (*.yml)|*.yml|All Files (*.*)|*.*",
                ".yml",
                "docker-compose.yml",
                "Export to Docker Compose");

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                try
                {
                    string yamlContent = GenerateYaml(sheet);
                    File.WriteAllText(filePath, yamlContent, Encoding.UTF8);
                    dialogService.ShowInfo($"파일이 생성되었습니다!\n{filePath}", "성공");
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
                    var networkKey = FindExistingKey(networks, netGroup.Title) ?? netGroup.Title;
                    networks[networkKey] = BuildNetworkDefinition(netGroup, containerNodes, ComposeYamlHelper.GetMapping(networks[networkKey]));
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
                    var volumeKey = FindExistingKey(volumeMap, vol.Name) ?? vol.Name;
                    volumeMap[volumeKey] = BuildVolumeDefinition(vol, ComposeYamlHelper.GetMapping(volumeMap[volumeKey]));
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

        private static object? FindExistingKey(Dictionary<object, object> map, string key)
        {
            return map.Keys.FirstOrDefault(existingKey =>
                string.Equals(existingKey?.ToString(), key, StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<object, object> BuildNetworkDefinition(
            GroupViewModel netGroup,
            List<NodeViewModel> containerNodes,
            Dictionary<object, object>? existingDefinition)
        {
            var netObj = ComposeYamlHelper.ParseMapping(netGroup.ComposeRawNetworkYaml)
                         ?? (existingDefinition != null
                             ? new Dictionary<object, object>(existingDefinition)
                             : new Dictionary<object, object>());

            if (netGroup.External)
            {
                netObj.Clear();
                netObj["external"] = true;
                if (!string.IsNullOrWhiteSpace(netGroup.ComposeNetworkName))
                    netObj["name"] = netGroup.ComposeNetworkName;
                return netObj;
            }

            RemoveComposeKey(netObj, "external");

            netObj["driver"] = string.IsNullOrWhiteSpace(netGroup.Driver) ? "bridge" : netGroup.Driver;
            SetBoolValue(netObj, "internal", netGroup.Internal);
            SetBoolValue(netObj, "attachable", netGroup.Attachable);
            SetBoolValue(netObj, "enable_ipv6", netGroup.EnableIPv6);
            SetStringValue(netObj, "name", netGroup.ComposeNetworkName);
            SetDictionaryValue(netObj, "labels", netGroup.Labels);
            SetDictionaryValue(netObj, "driver_opts", netGroup.DriverOptions);

            string? generatedSubnet = GetSubnetIfRequired(netGroup.Title, containerNodes);
            string? subnet = string.IsNullOrWhiteSpace(netGroup.Subnet) ? generatedSubnet : netGroup.Subnet;

            if (!string.IsNullOrWhiteSpace(subnet) ||
                !string.IsNullOrWhiteSpace(netGroup.Gateway) ||
                !string.IsNullOrWhiteSpace(netGroup.IpRange) ||
                netGroup.AuxAddresses.Count > 0)
            {
                var ipam = ComposeYamlHelper.GetMapping(ComposeYamlHelper.GetValue(netObj, "ipam")) != null
                    ? new Dictionary<object, object>(ComposeYamlHelper.GetMapping(ComposeYamlHelper.GetValue(netObj, "ipam"))!)
                    : new Dictionary<object, object>();

                var configList = ToMutableConfigList(ComposeYamlHelper.GetValue(ipam, "config"));
                var firstConfig = configList.Count > 0 && ComposeYamlHelper.GetMapping(configList[0]) != null
                    ? new Dictionary<object, object>(ComposeYamlHelper.GetMapping(configList[0])!)
                    : new Dictionary<object, object>();

                SetStringValue(firstConfig, "subnet", subnet);
                SetStringValue(firstConfig, "gateway", netGroup.Gateway);
                SetStringValue(firstConfig, "ip_range", netGroup.IpRange);
                SetDictionaryValue(firstConfig, "aux_addresses", netGroup.AuxAddresses);

                if (configList.Count == 0) configList.Add(firstConfig);
                else configList[0] = firstConfig;

                ipam["config"] = configList;
                netObj["ipam"] = ipam;
            }

            return netObj;
        }

        private static List<object> ToMutableConfigList(object? configValue)
        {
            if (configValue is IEnumerable<object> objectEnumerable)
                return objectEnumerable.ToList();

            if (configValue is System.Collections.IEnumerable enumerable)
                return enumerable.Cast<object>().ToList();

            return new List<object>();
        }

        private static void SetBoolValue(Dictionary<object, object> map, string key, bool value)
        {
            if (value) map[key] = true;
            else RemoveComposeKey(map, key);
        }

        private static void SetStringValue(Dictionary<object, object> map, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) map[key] = value;
            else RemoveComposeKey(map, key);
        }

        private static void SetDictionaryValue(Dictionary<object, object> map, string key, Dictionary<string, string> value)
        {
            if (value.Count > 0)
                map[key] = new Dictionary<object, object>(value.ToDictionary(kv => (object)kv.Key, kv => (object)kv.Value));
            else
                RemoveComposeKey(map, key);
        }

        private static Dictionary<object, object> BuildVolumeDefinition(NodeViewModel volumeNode, Dictionary<object, object>? existingDefinition)
        {
            var volObj = ComposeYamlHelper.ParseMapping(volumeNode.ComposeRawVolumeYaml)
                         ?? (existingDefinition != null
                             ? new Dictionary<object, object>(existingDefinition)
                             : new Dictionary<object, object>());

            if (volumeNode.VolumeExternal)
            {
                volObj["external"] = true;
                if (!string.IsNullOrWhiteSpace(volumeNode.DockerVolumeName) &&
                    !string.Equals(volumeNode.DockerVolumeName, volumeNode.Name, StringComparison.OrdinalIgnoreCase))
                {
                    volObj["name"] = volumeNode.DockerVolumeName;
                }
                else
                {
                    RemoveComposeKey(volObj, "name");
                }
                return volObj;
            }

            RemoveComposeKey(volObj, "external");
            SetStringValue(volObj, "name",
                !string.IsNullOrWhiteSpace(volumeNode.DockerVolumeName) &&
                !string.Equals(volumeNode.DockerVolumeName, volumeNode.Name, StringComparison.OrdinalIgnoreCase)
                    ? volumeNode.DockerVolumeName
                    : string.Empty);
            SetStringValue(volObj, "driver", volumeNode.Driver == "-" ? string.Empty : volumeNode.Driver);
            SetDictionaryValue(volObj, "labels", volumeNode.VolumeLabels);
            SetDictionaryValue(volObj, "driver_opts", volumeNode.VolumeDriverOptions);

            return volObj;
        }

        private static void RemoveComposeKey(Dictionary<object, object> map, string key)
        {
            var existingKey = FindExistingKey(map, key);
            if (existingKey != null) map.Remove(existingKey);
        }

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
            bool hasEndpointOptions = connectedNets.Any(n => n.Options?.HasAnyOption == true);
            if (!hasEndpointOptions)
                return connectedNets.Select(n => n.NetworkName!).ToList();

            var netDict = new Dictionary<object, object>();
            foreach (var net in connectedNets)
            {
                var netConfig = new Dictionary<object, object>();
                if (!string.IsNullOrEmpty(net.Options?.StaticIPv4) && IsValidIp(net.Options.StaticIPv4))
                    netConfig["ipv4_address"] = net.Options.StaticIPv4;
                if (!string.IsNullOrWhiteSpace(net.Options?.StaticIPv6))
                    netConfig["ipv6_address"] = net.Options.StaticIPv6;
                if (net.Options?.Aliases.Count > 0)
                    netConfig["aliases"] = net.Options.Aliases;
                if (net.Options?.DriverOptions.Count > 0)
                    netConfig["driver_opts"] = new Dictionary<object, object>(
                        net.Options.DriverOptions.ToDictionary(kv => (object)kv.Key, kv => (object)kv.Value));

                netDict[net.NetworkName!] = netConfig;
            }
            return netDict;
        }

        private class NetworkInfo { public string? NetworkName; public ContainerNetworkOptions? Options; }

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
                    list.Add(new NetworkInfo { NetworkName = group.Title, Options = container.GetNetworkOptions(group.Title) });
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
                var options = container.GetNetworkOptions(networkName);
                if (options != null && IsValidIp(options.StaticIPv4))
                {
                    var parts = options.StaticIPv4.Split('.');
                    if (parts.Length == 4) return $"{parts[0]}.{parts[1]}.{parts[2]}.0/24";
                }
            }
            return null;
        }
    }
}
