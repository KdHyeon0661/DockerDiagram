using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
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

                    // ★ [수정] ShowInfo 사용
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
            sb.AppendLine("version: '3.8'");
            sb.AppendLine("services:");

            // 서비스 이름 확정 (중복 방지 매핑)
            var nodeIdToServiceName = new Dictionary<string, string>();
            var usedServiceNames = new HashSet<string>();

            var containerNodes = sheet.Nodes.Where(n => n.Type == NodeType.Container).ToList();

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

                // 볼륨 연결은 커넥터(선) 방식 유지 (Node <-> Node)
                var connectedVolumes = GetConnectedVolumes(node, sheet);
                if (connectedVolumes.Count > 0)
                {
                    sb.AppendLine("    volumes:");
                    foreach (var vol in connectedVolumes)
                        sb.AppendLine($"      - \"{vol}\"");
                }

                // 의존성(depends_on)도 커넥터(선) 방식 유지
                var depConns = sheet.Connectors
                    .Where(c => c.Target == node
                             && c.Source.Type == NodeType.Container
                             && c.RelationType == RelationType.Dependency)
                    .ToList();

                if (depConns.Count > 0)
                {
                    sb.AppendLine("    depends_on:");
                    foreach (var conn in depConns)
                    {
                        if (nodeIdToServiceName.TryGetValue(conn.Source.Id, out string? depServiceName))
                        {
                            sb.AppendLine($"      - {depServiceName}");
                        }
                    }
                }

                // ★ [수정] 네트워크 정보 조회 로직 변경 (Node -> Group)
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

            // ★ [수정] 네트워크 정의 (Nodes가 아니라 Groups에서 찾기)
            var networkGroups = sheet.Groups.Where(g => g.Type == GroupType.Network).ToList();
            if (networkGroups.Any())
            {
                sb.AppendLine("networks:");
                foreach (var netGroup in networkGroups)
                {
                    sb.AppendLine($"  {netGroup.Title}:");
                    // Group에는 ImageName이 없으므로 기본값 bridge 사용
                    string driver = "bridge";
                    sb.AppendLine($"    driver: {driver}");

                    // 서브넷/게이트웨이 정보는 GroupViewModel에 현재 없으므로 생략하거나
                    // 추후 GroupViewModel에 속성을 추가해야 함. 현재는 기본 설정만 출력.
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
                    // 바인드 마운트(경로)가 아닌 명명된 볼륨만 정의
                    if (!vol.Name.Contains("/") && !vol.Name.Contains("\\"))
                    {
                        sb.AppendLine($"  {vol.Name}:");
                    }
                }
            }

            return sb.ToString();
        }

        // --- 헬퍼 메서드들 (이름 정제 등 기존 로직 100% 유지) ---

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
            var conns = sheet.Connectors
                .Where(c => (c.Source == container && c.Target.Type == NodeType.Volume) ||
                            (c.Target == container && c.Source.Type == NodeType.Volume))
                .ToList();

            foreach (var c in conns)
            {
                var volNode = c.Source == container ? c.Target : c.Source;
                string mountPath = !string.IsNullOrEmpty(c.MountPath) ? c.MountPath : "/data";
                list.Add($"{volNode.Name}:{mountPath}");
            }
            return list;
        }

        private class NetworkInfo { public string? NetworkName; public string? IpAddress; }

        // ★ [수정] 네트워크 연결 확인 로직 (커넥터 -> 그룹 포함 여부로 변경)
        private static List<NetworkInfo> GetConnectedNetworks(NodeViewModel container, SheetViewModel sheet)
        {
            var list = new List<NetworkInfo>();

            // 1. 시트에 있는 모든 네트워크 그룹을 순회
            var networkGroups = sheet.Groups.Where(g => g.Type == GroupType.Network);

            foreach (var group in networkGroups)
            {
                // 2. 해당 그룹에 컨테이너가 포함되어 있는지 확인
                if (group.ContainedNodes.Contains(container))
                {
                    string ip = null;
                    // IP 정보가 있다면 가져오기 (컨테이너의 맵에서 그룹 이름으로 조회)
                    if (container.NetworkIpMap != null && container.NetworkIpMap.ContainsKey(group.Title))
                        ip = container.NetworkIpMap[group.Title];

                    list.Add(new NetworkInfo { NetworkName = group.Title, IpAddress = ip });
                }
            }
            return list;
        }
    }
}