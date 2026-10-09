using DockerDiagram.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DockerDiagram.Infrastructure
{
    /// <summary>
    /// Pins Docker CLI invocations to the same endpoint selected by the application.
    /// This prevents inherited DOCKER_HOST/DOCKER_CONTEXT variables from targeting a different engine.
    /// </summary>
    public static class DockerCliTargetEnvironment
    {
        public static string ResolveDockerHost(ConnectionProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            return profile.Type switch
            {
                EndpointType.SshRemote when profile.LocalTunnelPort > 0 =>
                    $"tcp://127.0.0.1:{profile.LocalTunnelPort}",
                EndpointType.DockerContext when !string.IsNullOrWhiteSpace(profile.DockerEndpoint) =>
                    profile.DockerEndpoint.Trim(),
                EndpointType.Local when RuntimeInformation.IsOSPlatform(OSPlatform.Windows) =>
                    "npipe://./pipe/docker_engine",
                EndpointType.Local => "unix:///var/run/docker.sock",
                _ => throw new InvalidOperationException("선택한 연결의 Docker CLI endpoint를 확인할 수 없습니다.")
            };
        }

        public static void Apply(ProcessStartInfo startInfo, ConnectionProfile profile)
        {
            ArgumentNullException.ThrowIfNull(startInfo);
            startInfo.Environment.Remove("DOCKER_CONTEXT");
            startInfo.Environment.Remove("DOCKER_HOST");
            startInfo.Environment["DOCKER_HOST"] = ResolveDockerHost(profile);
        }
    }
}
