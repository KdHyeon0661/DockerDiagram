using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

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

            // 1. Header
            sb.AppendLine("version: '3.8'");
            sb.AppendLine();

            // 2. Services (Containers)
            var containers = sheet.Nodes.Where(n => n.Type == NodeType.Container).ToList();
            if (containers.Any())
            {
                sb.AppendLine("services:");
                foreach (var node in containers)
                {
                    // 서비스 이름은 공백 제거하고 소문자로 (예: "My DB" -> "my_db")
                    string serviceName = node.Name.Replace(" ", "_").ToLower();

                    sb.AppendLine($"  {serviceName}:");
                    sb.AppendLine($"    container_name: {node.Name}");
                    sb.AppendLine($"    image: {node.ImageName}");
                    sb.AppendLine($"    restart: always");

                    // 2-1. Ports
                    // (NodeViewModel에 PortBindings가 List<string> 형태인 "8080:80" 등으로 있다고 가정)
                    if (node.PortBindings != null && node.PortBindings.Count > 0)
                    {
                        sb.AppendLine("    ports:");
                        foreach (var port in node.PortBindings)
                        {
                            sb.AppendLine($"      - \"{port}\"");
                        }
                    }

                    // 2-2. Environment Variables
                    if (node.EnvironmentVariables != null && node.EnvironmentVariables.Count > 0)
                    {
                        sb.AppendLine("    environment:");
                        foreach (var env in node.EnvironmentVariables)
                        {
                            sb.AppendLine($"      - {env}");
                        }
                    }

                    // 2-3. Volumes (Connected Volumes)
                    // 현재 컨테이너와 연결된 VolumeMount 커넥터 찾기
                    var volConns = sheet.Connectors.Where(c =>
                        (c.Source == node && c.Target.Type == NodeType.Volume) ||
                        (c.Target == node && c.Source.Type == NodeType.Volume)
                    ).ToList();

                    if (volConns.Count > 0)
                    {
                        sb.AppendLine("    volumes:");
                        foreach (var conn in volConns)
                        {
                            var volNode = conn.Source == node ? conn.Target : conn.Source;
                            // 주의: 볼륨 마운트 경로는 Connector나 별도 속성에 저장되어 있어야 함.
                            // 여기서는 기본적으로 "/data" 등으로 예시를 들거나, 
                            // 만약 ConnectorViewModel에 마운트 경로 속성이 있다면 그걸 써야 함.
                            // 현재는 단순하게 "볼륨이름:/app/data" 형식으로 가정합니다.
                            string path = !string.IsNullOrWhiteSpace(conn.MountPath) ? conn.MountPath : "/app/data";
                            sb.AppendLine($"      - {volNode.Name}:{path}");
                        }
                    }

                    // 2-4. Networks (Connected Networks)
                    var netConns = sheet.Connectors.Where(c =>
                        (c.Source == node && c.Target.Type == NodeType.Network)
                    ).ToList();

                    if (netConns.Count > 0)
                    {
                        sb.AppendLine("    networks:");
                        foreach (var conn in netConns)
                        {
                            var netNode = conn.Target; // Network

                            //  고정 IP 요청이 있는가?
                            if (!string.IsNullOrWhiteSpace(conn.IpAddress))
                            {
                                sb.AppendLine($"      {netNode.Name}:");
                                sb.AppendLine($"        ipv4_address: {conn.IpAddress}");
                            }
                            else  // 그냥 연결만
                            {
                                sb.AppendLine($"      - {netNode.Name}");
                            }
                        }
                    }

                    // 2-5. Depends_on (순서 연결선)
                    var depConns = sheet.Connectors.Where(c => c.Target == node && c.RelationType == DockerDiagram.ViewModels.RelationType.Dependency).ToList();
                    if (depConns.Count > 0)
                    {
                        sb.AppendLine("    depends_on:");
                        foreach (var conn in depConns)
                        {
                            string depName = conn.Source.Name.Replace(" ", "_").ToLower();
                            sb.AppendLine($"      - {depName}");
                        }
                    }

                    sb.AppendLine(); // 서비스 간 공백
                }
            }

            // 3. Top-level Networks
            var networks = sheet.Nodes.Where(n => n.Type == NodeType.Network).ToList();
            if (networks.Any())
            {
                sb.AppendLine("networks:");
                foreach (var net in networks)
                {
                    sb.AppendLine($"  {net.Name}:");
                    sb.AppendLine("    driver: bridge");

                }
                sb.AppendLine();
            }

            // 4. Top-level Volumes
            var volumes = sheet.Nodes.Where(n => n.Type == NodeType.Volume).ToList();
            if (volumes.Any())
            {
                sb.AppendLine("volumes:");
                foreach (var vol in volumes)
                {
                    sb.AppendLine($"  {vol.Name}:");
                }
            }

            return sb.ToString();
        }
    }
}