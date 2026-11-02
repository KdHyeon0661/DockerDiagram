using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DockerDiagram.Models;

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
                    "npipe:////./pipe/docker_engine",
                EndpointType.Local => "unix:///var/run/docker.sock",
                _ => throw new InvalidOperationException("선택한 연결의 Docker CLI endpoint를 확인할 수 없습니다.")
            };
        }

        public static void Apply(ProcessStartInfo startInfo, ConnectionProfile profile)
        {
            ArgumentNullException.ThrowIfNull(startInfo);
            startInfo.Environment.Remove("DOCKER_CONTEXT");
            startInfo.Environment.Remove("DOCKER_HOST");
            startInfo.Environment.Remove("DOCKER_TLS");
            startInfo.Environment.Remove("DOCKER_TLS_VERIFY");
            startInfo.Environment.Remove("DOCKER_CERT_PATH");

            // 이름이 보존된 Docker context는 CLI가 자체 TLS/SSH 설정을 그대로 사용하게 합니다.
            if (profile.Type == EndpointType.DockerContext && !string.IsNullOrWhiteSpace(profile.DockerContextName))
            {
                startInfo.Environment["DOCKER_CONTEXT"] = profile.DockerContextName.Trim();
                return;
            }

            startInfo.Environment["DOCKER_HOST"] = ResolveDockerHost(profile);

            if (profile.Type == EndpointType.DockerContext && profile.DockerTlsEnabled)
            {
                startInfo.Environment["DOCKER_TLS"] = "1";
                if (!profile.DockerTlsSkipVerify)
                    startInfo.Environment["DOCKER_TLS_VERIFY"] = "1";

                string? certificatePath = profile.DockerTlsCertPath ?? profile.DockerTlsCaPath;
                string? certificateDirectory = string.IsNullOrWhiteSpace(certificatePath)
                    ? null
                    : Path.GetDirectoryName(certificatePath);
                if (!string.IsNullOrWhiteSpace(certificateDirectory))
                    startInfo.Environment["DOCKER_CERT_PATH"] = certificateDirectory;
            }
        }
    }
}
