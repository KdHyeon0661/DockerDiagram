using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

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
            var sb = new StringBuilder();

            // version: '3.8' 완전 삭제 (최신 Compose V2 표준 준수)
            sb.AppendLine("services:");

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

            // YAML 생성
            foreach (var node in containerNodes)
            {
                string serviceName = nodeIdToServiceName[node.Id];
                sb.AppendLine($"  {serviceName}:");

                string safeContainerName = SanitizeContainerName(node.Name);
                sb.AppendLine($"    container_name: \"{safeContainerName}\"");

                string image = !string.IsNullOrWhiteSpace(node.ImageName) ? node.ImageName : "nginx:latest";
                sb.AppendLine($"    image: \"{image}\"");

                if (!string.IsNullOrEmpty(node.RestartPolicy) && node.RestartPolicy != "no")
                {
                    sb.AppendLine($"    restart: {node.RestartPolicy}");
                }

                if (node.PortBindings != null && node.PortBindings.Count > 0)
                {
                    sb.AppendLine("    ports:");
                    foreach (var port in node.PortBindings)
                        sb.AppendLine($"      - \"{port}\"");
                }

                if (node.EnvironmentVariables != null && node.EnvironmentVariables.Count > 0)
                {
                    sb.AppendLine("    environment:");
                    foreach (var env in node.EnvironmentVariables)
                        sb.AppendLine($"      - \"{EscapeQuotes(env)}\"");
                }

                // 볼륨 연결은 커넥터(선) 방식 유지
                var connectedVolumes = GetConnectedVolumes(node, sheet);
                if (connectedVolumes.Count > 0)
                {
                    sb.AppendLine("    volumes:");
                    foreach (var vol in connectedVolumes)
                        sb.AppendLine($"      - \"{vol}\"");
                }

                // ★ [수정] 의존성(depends_on): Target이 NodeViewModel인지 안전하게 검사
                var depConns = sheet.Connectors
                    .Where(c => c.Source == node
                             && c.RelationType == RelationType.Dependency
                             && c.Target is NodeViewModel targetNode && targetNode.Type == NodeType.Container)
                    .ToList();

                if (depConns.Count > 0)
                {
                    sb.AppendLine("    depends_on:");
                    foreach (var conn in depConns)
                    {
                        if (nodeIdToServiceName.TryGetValue(conn.Target.Id, out string? depServiceName))
                        {
                            sb.AppendLine($"      - {depServiceName}");
                        }
                    }
                }

                // 네트워크 정보 조회 (Node -> Group)
                var connectedNets = GetConnectedNetworks(node, sheet);
                if (connectedNets.Count > 0)
                {
                    sb.AppendLine("    networks:");
                    foreach (var netInfo in connectedNets)
                    {
                        sb.AppendLine($"      {netInfo.NetworkName}:");
                        if (!string.IsNullOrEmpty(netInfo.IpAddress) && IsValidIp(netInfo.IpAddress))
                        {
                            sb.AppendLine($"        ipv4_address: {netInfo.IpAddress}");
                        }
                    }
                }
                sb.AppendLine();
            }

            // 네트워크 정의 (Groups에서 찾기)
            var networkGroups = sheet.Groups.Where(g => g.Type == GroupType.Network).ToList();
            if (networkGroups.Any())
            {
                sb.AppendLine("networks:");
                foreach (var netGroup in networkGroups)
                {
                    sb.AppendLine($"  {netGroup.Title}:");
                    sb.AppendLine($"    driver: bridge");

                    // 정적 IP 할당 버그 방어: 해당 네트워크에 고정 IP를 쓴 컨테이너가 있다면 강제로 ipam(Subnet) 블록을 생성해 줍니다.
                    string generatedSubnet = GetSubnetIfRequired(netGroup.Title, containerNodes);
                    if (!string.IsNullOrEmpty(generatedSubnet))
                    {
                        sb.AppendLine("    ipam:");
                        sb.AppendLine("      config:");
                        sb.AppendLine($"        - subnet: {generatedSubnet}");
                    }
                }
                sb.AppendLine();
            }

            // 볼륨 정의 (기존 유지 - 볼륨은 여전히 Node임)
            var volumes = sheet.Nodes.Where(n => n.Type == NodeType.Volume).ToList();
            if (volumes.Any())
            {
                sb.AppendLine("volumes:");
                foreach (var vol in volumes)
                {
                    // 강력해진 볼륨 구별 로직: 단순 슬래시 포함 여부가 아니라 '경로 형태'인지 명확히 검사합니다.
                    bool isBindMount = vol.Name.StartsWith("/") ||
                                       vol.Name.StartsWith("./") ||
                                       vol.Name.StartsWith("../") ||
                                       vol.Name.StartsWith("~/") ||
                                       Regex.IsMatch(vol.Name, @"^[a-zA-Z]:[\\/]");

                    if (!isBindMount)
                    {
                        // 명명된 볼륨(Named Volume)만 루트에 생성
                        sb.AppendLine($"  {vol.Name}:");
                    }
                }
            }

            return sb.ToString();
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

        private static string EscapeQuotes(string input) => input.Replace("\"", "\\\"");

        private static bool IsValidIp(string ip) => !string.IsNullOrWhiteSpace(ip) && ip.Count(c => c == '.') == 3;

        private static List<string> GetConnectedVolumes(NodeViewModel container, SheetViewModel sheet)
        {
            var list = new List<string>();

            // ★ [수정] Connector의 Source/Target이 IConnectableItem이므로 NodeViewModel인지 안전하게 캐스팅
            var conns = sheet.Connectors
                .Where(c => (c.Source == container && c.Target is NodeViewModel tNode1 && tNode1.Type == NodeType.Volume) ||
                            (c.Target == container && c.Source is NodeViewModel sNode2 && sNode2.Type == NodeType.Volume))
                .ToList();

            foreach (var c in conns)
            {
                // Source가 자기 자신이면 Target을 가져오고, 아니면 Source를 가져옴 (반드시 NodeViewModel임)
                var volNode = c.Source == container ? (NodeViewModel)c.Target : (NodeViewModel)c.Source;
                string mountPath = !string.IsNullOrEmpty(c.MountPath) ? c.MountPath : "/data";
                list.Add($"{volNode.Name}:{mountPath}");
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
                    string ip = null;
                    if (container.NetworkIpMap != null && container.NetworkIpMap.ContainsKey(group.Title))
                        ip = container.NetworkIpMap[group.Title];

                    list.Add(new NetworkInfo { NetworkName = group.Title, IpAddress = ip });
                }
            }
            return list;
        }

        // 서브넷 자동 계산 헬퍼 로직 (네트워크 에러 방지용)
        private static string GetSubnetIfRequired(string networkName, List<NodeViewModel> containers)
        {
            foreach (var container in containers)
            {
                if (container.NetworkIpMap != null &&
                    container.NetworkIpMap.TryGetValue(networkName, out string ip) &&
                    IsValidIp(ip))
                {
                    var parts = ip.Split('.');
                    if (parts.Length == 4)
                    {
                        return $"{parts[0]}.{parts[1]}.{parts[2]}.0/24";
                    }
                }
            }
            return null;
        }
    }
}