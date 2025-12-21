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
        // compose로 파일 내보내기
        public static void ExportToCompose(SheetViewModel sheet)
        {
            if (sheet == null || sheet.Nodes.Count == 0) // 아무것도 없으면
            {
                MessageBox.Show("내보낼 노드가 없습니다.", "알림");
                return;
            }

            var dlg = new SaveFileDialog // 저장 대화상자
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

            // 헤더
            sb.AppendLine("version: '3.8'");
            sb.AppendLine();

            // 컨테이너
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

                    // 재시작 정책
                    string policy = !string.IsNullOrWhiteSpace(node.RestartPolicy) ? node.RestartPolicy : "no";
                    sb.AppendLine($"    restart: {policy}");

                    // 포트. NodeViewModel에 PortBindings가 List<string> 형태인 "8080:80" 등으로 있다고 가정
                    if (node.PortBindings != null && node.PortBindings.Count > 0)
                    {
                        sb.AppendLine("    ports:");
                        foreach (var port in node.PortBindings)
                        {
                            sb.AppendLine($"      - \"{port}\"");
                        }
                    }

                    // 환경 변수
                    if (node.EnvironmentVariables != null && node.EnvironmentVariables.Count > 0)
                    {
                        sb.AppendLine("    environment:");
                        foreach (var env in node.EnvironmentVariables)
                        {
                            sb.AppendLine($"      - {env}");
                        }
                    }

                    // 연결된 볼륨
                    var volConns = sheet.Connectors.Where(c => c.Source == node && c.Target.Type == NodeType.Volume).ToList(); // MainViewModel에서 container->volume 연결만 생성

                    if (volConns.Count > 0)
                    {
                        sb.AppendLine("    volumes:");
                        foreach (var conn in volConns)
                        {
                            var volNode = conn.Source == node ? conn.Target : conn.Source;
                            string path = !string.IsNullOrWhiteSpace(conn.MountPath) ? conn.MountPath : "FIXME_MISSING_PATH";
                            sb.AppendLine($"      - {volNode.Name}:{path}");
                        }
                    }

                    // 연결된 네트워크
                    var netConns = sheet.Connectors.Where(c =>
                        (c.Source == node && c.Target.Type == NodeType.Network)
                    ).ToList();

                    if (netConns.Count > 0)
                    {
                        sb.AppendLine("    networks:");
                        foreach (var conn in netConns)
                        {
                            var netNode = conn.Target;

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

                    // 연결선 - 의존
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

            // 네트워크
            var networks = sheet.Nodes.Where(n => n.Type == NodeType.Network).ToList();
            if (networks.Any())
            {
                sb.AppendLine("networks:");
                foreach (var net in networks)
                {
                    sb.AppendLine($"  {net.Name}:");
                    // 네트워크 드라이버
                    string driver = !string.IsNullOrWhiteSpace(net.ImageName) ? net.ImageName : "bridge";
                    sb.AppendLine($"    driver: {driver}");

                }
                sb.AppendLine();
            }

            // 볼륨
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