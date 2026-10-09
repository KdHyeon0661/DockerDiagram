using DockerDiagram.Models;

namespace DockerDiagram.ApplicationServices
{
    public static class SwarmServiceNodeProjection
    {
        public static string ModeText(SwarmServiceSpecOptions spec) =>
            spec.Mode == SwarmServiceModeKind.Global ? "global" : "replicated";

        public static string RestartText(SwarmRestartPolicyOptions policy) => policy.Condition switch
        {
            SwarmRestartCondition.None => "none",
            SwarmRestartCondition.OnFailure => "on-failure",
            _ => "any"
        };

        public static List<string> PortBindings(SwarmServiceSpecOptions spec) =>
            spec.PublishedPorts.Select(port =>
            {
                string published = port.PublishedPort?.ToString() ?? "auto";
                return $"{published}->{port.TargetPort}/{port.Protocol.ToString().ToLowerInvariant()} ({port.PublishMode.ToString().ToLowerInvariant()})";
            }).ToList();

        public static string InitialSummary(SwarmServiceSpecOptions spec)
        {
            string replicas = spec.Mode == SwarmServiceModeKind.Global
                ? "global / 0 running"
                : $"0/{spec.Replicas ?? 0}";
            string ports = string.Join(", ", PortBindings(spec));
            return ports.Length == 0 ? replicas : $"{replicas} · {ports}";
        }
    }
}
