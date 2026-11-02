using System.Collections;
using System.IO;
using DockerDiagram.Models;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace DockerDiagram.ApplicationServices
{
    public enum SwarmStackPreflightSeverity
    {
        Warning,
        Blocked
    }

    public sealed record SwarmStackPreflightIssue(
        SwarmStackPreflightSeverity Severity,
        string Message);

    public sealed record SwarmStackPreflightReport(
        IReadOnlyList<SwarmStackPreflightIssue> Issues)
    {
        public IReadOnlyList<SwarmStackPreflightIssue> BlockingIssues =>
            Issues.Where(issue => issue.Severity == SwarmStackPreflightSeverity.Blocked).ToArray();

        public IReadOnlyList<SwarmStackPreflightIssue> Warnings =>
            Issues.Where(issue => issue.Severity == SwarmStackPreflightSeverity.Warning).ToArray();
    }

    /// <summary>
    /// Detects Compose options that docker stack deploy cannot safely execute by itself.
    /// The analyzer never guesses host paths or registry availability; it exposes those
    /// deployment responsibilities before Docker is allowed to change the cluster.
    /// </summary>
    public static class SwarmStackPreflightAnalyzer
    {
        private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

        public static SwarmStackPreflightReport Analyze(string yaml, int? swarmNodeCount = null)
        {
            var issues = new List<SwarmStackPreflightIssue>();
            Dictionary<object, object>? root;
            try
            {
                root = Deserializer.Deserialize<Dictionary<object, object>>(yaml);
            }
            catch (YamlException ex)
            {
                return new SwarmStackPreflightReport([
                    Blocked($"Stack YAML을 해석할 수 없습니다: {ex.Message}")
                ]);
            }

            Dictionary<object, object>? services = ComposeYamlHelper.GetMapping(
                ComposeYamlHelper.GetValue(root, "services"));
            if (services == null || services.Count == 0)
            {
                return new SwarmStackPreflightReport([
                    Blocked("Stack YAML에 하나 이상의 services 항목이 필요합니다.")
                ]);
            }

            bool multiNode = swarmNodeCount is > 1;
            bool registryReminderAdded = false;
            foreach ((object serviceKey, object? rawService) in services)
            {
                string serviceName = serviceKey?.ToString() ?? "<unknown>";
                Dictionary<object, object>? service = ComposeYamlHelper.GetMapping(rawService);
                if (service == null)
                {
                    issues.Add(Blocked($"서비스 '{serviceName}' 설정은 YAML mapping이어야 합니다."));
                    continue;
                }

                object? build = ComposeYamlHelper.GetValue(service, "build");
                string image = ComposeYamlHelper.GetValue(service, "image")?.ToString()?.Trim() ?? string.Empty;
                if (build != null && string.IsNullOrWhiteSpace(image))
                {
                    issues.Add(Blocked(
                        $"서비스 '{serviceName}'은 build만 지정했습니다. Swarm은 이미지를 빌드하지 않으므로 " +
                        "Registry에 push한 image를 지정해야 합니다."));
                }
                else if (build != null)
                {
                    issues.Add(Warning(
                        $"서비스 '{serviceName}'의 build는 Swarm에서 무시됩니다. 배포에는 image '{image}'가 사용되며 " +
                        "모든 대상 노드에서 pull 가능해야 합니다."));
                }

                if (multiNode && !string.IsNullOrWhiteSpace(image) && !registryReminderAdded)
                {
                    issues.Add(Warning(
                        $"현재 {swarmNodeCount}개 노드가 있습니다. 모든 서비스 image가 각 노드에서 pull 가능한 " +
                        "Registry에 있어야 하며, private Registry라면 'Send registry auth'를 사용해야 합니다."));
                    registryReminderAdded = true;
                }

                if (multiNode)
                    AnalyzeMultiNodeMounts(root, serviceName, service, issues, swarmNodeCount!.Value);
            }

            return new SwarmStackPreflightReport(issues.Distinct().ToArray());
        }

        private static void AnalyzeMultiNodeMounts(
            Dictionary<object, object>? root,
            string serviceName,
            Dictionary<object, object> service,
            List<SwarmStackPreflightIssue> issues,
            int nodeCount)
        {
            object? rawVolumes = ComposeYamlHelper.GetValue(service, "volumes");
            if (rawVolumes is not IEnumerable values || rawVolumes is string || rawVolumes is IDictionary)
                return;

            foreach (object? rawMount in values)
            {
                if (!TryDescribeMount(root, rawMount, out MountRisk risk, out string source))
                    continue;

                if (risk == MountRisk.Bind)
                {
                    issues.Add(Warning(
                        $"{nodeCount}노드 Swarm에서 서비스 '{serviceName}'의 bind mount '{source}' 경로는 " +
                        "작업이 배치될 모든 노드에 같은 경로와 데이터로 존재해야 합니다. 필요하면 placement constraint를 설정하세요."));
                }
                else if (risk == MountRisk.LocalVolume)
                {
                    issues.Add(Warning(
                        $"{nodeCount}노드 Swarm에서 서비스 '{serviceName}'의 local volume '{source}' 데이터는 " +
                        "노드 간 자동 공유되지 않습니다. 공유 스토리지 드라이버 또는 placement constraint가 필요합니다."));
                }
            }
        }

        private static bool TryDescribeMount(
            Dictionary<object, object>? root,
            object? rawMount,
            out MountRisk risk,
            out string source)
        {
            risk = MountRisk.None;
            source = string.Empty;

            if (rawMount is string shortSyntax)
            {
                if (!TryGetShortMountSource(shortSyntax, out source))
                {
                    source = "anonymous";
                    risk = MountRisk.LocalVolume;
                    return true;
                }

                risk = LooksLikeBindSource(source)
                    ? MountRisk.Bind
                    : IsLocalVolume(root, source) ? MountRisk.LocalVolume : MountRisk.None;
                return risk != MountRisk.None;
            }

            Dictionary<object, object>? mount = ComposeYamlHelper.GetMapping(rawMount);
            if (mount == null) return false;

            string type = ComposeYamlHelper.GetValue(mount, "type")?.ToString()?.Trim().ToLowerInvariant() ?? "volume";
            source = ComposeYamlHelper.GetValue(mount, "source")?.ToString()?.Trim() ?? "anonymous";
            risk = type switch
            {
                "bind" => MountRisk.Bind,
                "volume" when source == "anonymous" || IsLocalVolume(root, source) => MountRisk.LocalVolume,
                _ => MountRisk.None
            };
            return risk != MountRisk.None;
        }

        private static bool TryGetShortMountSource(string value, out string source)
        {
            source = string.Empty;
            string mount = value.Trim();
            int separator = mount.IndexOf(':');
            bool windowsDrive = separator == 1 && mount.Length > 2 && char.IsLetter(mount[0]) &&
                                (mount[2] == '\\' || mount[2] == '/');
            if (windowsDrive) separator = mount.IndexOf(':', 3);
            if (separator <= 0) return false;

            source = mount[..separator].Trim();
            return source.Length > 0;
        }

        private static bool LooksLikeBindSource(string source) =>
            source.StartsWith('.') ||
            source.StartsWith('/') ||
            source.StartsWith('\\') ||
            source.StartsWith('~') ||
            Path.IsPathRooted(source);

        private static bool IsLocalVolume(Dictionary<object, object>? root, string name)
        {
            Dictionary<object, object>? volumes = ComposeYamlHelper.GetMapping(
                ComposeYamlHelper.GetValue(root, "volumes"));
            Dictionary<object, object>? definition = ComposeYamlHelper.GetMapping(
                ComposeYamlHelper.GetValue(volumes, name));
            string driver = ComposeYamlHelper.GetValue(definition, "driver")?.ToString()?.Trim() ?? "local";
            return driver.Equals("local", StringComparison.OrdinalIgnoreCase);
        }

        private static SwarmStackPreflightIssue Warning(string message) =>
            new(SwarmStackPreflightSeverity.Warning, message);

        private static SwarmStackPreflightIssue Blocked(string message) =>
            new(SwarmStackPreflightSeverity.Blocked, message);

        private enum MountRisk
        {
            None,
            Bind,
            LocalVolume
        }
    }
}
