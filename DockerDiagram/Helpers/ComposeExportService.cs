using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;
using DockerDiagram.Models; // RelationType이 여기 있습니다.
using DockerDiagram.ViewModels;
using System.Collections.Generic;
using System.Linq;

namespace DockerDiagram.Helpers
{
    public static class ComposeExportService
    {
        public static void ExportToCompose(SheetViewModel sheet)
        {
            if (sheet == null || sheet.Nodes.Count == 0)
            {
                MessageBox.Show("내보낼 노드가 없습니다.", "알림");
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
                    MessageBox.Show($"파일이 생성되었습니다!\n{dlg.FileName}", "성공", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"내보내기 실패: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private static string GenerateYaml(SheetViewModel sheet)
        {
            var sb = new StringBuilder();
            sb.AppendLine("version: '3.8'");
            sb.AppendLine("services:");

            // [Pass 1] 서비스 이름 확정 (중복 방지 매핑)
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

            // [Pass 2] YAML 생성
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

                var connectedVolumes = GetConnectedVolumes(node, sheet);
                if (connectedVolumes.Count > 0)
                {
                    sb.AppendLine("    volumes:");
                    foreach (var vol in connectedVolumes)
                        sb.AppendLine($"      - \"{vol}\"");
                }

                // ★ RelationType 사용 부분 (이제 깔끔하게 Dependency 사용)
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
                        if (nodeIdToServiceName.TryGetValue(conn.Source.Id, out string depServiceName))
                        {
                            sb.AppendLine($"      - {depServiceName}");
                        }
                    }
                }

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

            // 네트워크 정의
            var networks = sheet.Nodes.Where(n => n.Type == NodeType.Network).ToList();
            if (networks.Any())
            {
                sb.AppendLine("networks:");
                foreach (var net in networks)
                {
                    sb.AppendLine($"  {net.Name}:");
                    string driver = !string.IsNullOrWhiteSpace(net.ImageName) ? net.ImageName : "bridge";
                    sb.AppendLine($"    driver: {driver}");

                    if (!string.IsNullOrEmpty(net.Subnet) && net.Subnet != "-")
                    {
                        sb.AppendLine("    ipam:");
                        sb.AppendLine("      config:");
                        sb.AppendLine($"        - subnet: {net.Subnet}");
                        if (!string.IsNullOrEmpty(net.Gateway) && net.Gateway != "-")
                            sb.AppendLine($"          gateway: {net.Gateway}");
                    }
                }
                sb.AppendLine();
            }

            // 볼륨 정의
            var volumes = sheet.Nodes.Where(n => n.Type == NodeType.Volume).ToList();
            if (volumes.Any())
            {
                sb.AppendLine("volumes:");
                foreach (var vol in volumes)
                {
                    if (!vol.Name.Contains("/") && !vol.Name.Contains("\\"))
                    {
                        sb.AppendLine($"  {vol.Name}:");
                    }
                }
            }

            return sb.ToString();
        }

        // --- Helper Methods ---
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

        private class NetworkInfo { public string NetworkName; public string IpAddress; }

        private static List<NetworkInfo> GetConnectedNetworks(NodeViewModel container, SheetViewModel sheet)
        {
            var list = new List<NetworkInfo>();
            var conns = sheet.Connectors
                .Where(c => (c.Source == container && c.Target.Type == NodeType.Network) ||
                            (c.Target == container && c.Source.Type == NodeType.Network))
                .ToList();

            foreach (var c in conns)
            {
                var netNode = c.Source == container ? c.Target : c.Source;
                string ip = null;
                if (container.NetworkIpMap != null && container.NetworkIpMap.ContainsKey(netNode.Name))
                    ip = container.NetworkIpMap[netNode.Name];

                list.Add(new NetworkInfo { NetworkName = netNode.Name, IpAddress = ip });
            }
            return list;
        }
    }
}