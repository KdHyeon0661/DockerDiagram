using DockerDiagram.Models;
using System.Globalization;

namespace DockerDiagram.ApplicationServices
{
    public static class SwarmPublishedPortInputParser
    {
        public static SwarmPublishedPortOptions Parse(
            string? publishedPort,
            string? targetPort,
            string? protocol,
            string? publishMode)
        {
            if (!TryParse(
                    publishedPort,
                    targetPort,
                    protocol,
                    publishMode,
                    out SwarmPublishedPortOptions options,
                    out string error))
            {
                throw new ArgumentException(error);
            }

            return options;
        }

        public static bool TryParse(
            string? publishedPort,
            string? targetPort,
            string? protocol,
            string? publishMode,
            out SwarmPublishedPortOptions options,
            out string error)
        {
            options = new SwarmPublishedPortOptions(80);
            error = string.Empty;

            string normalizedTarget = targetPort?.Trim() ?? string.Empty;
            if (!uint.TryParse(
                    normalizedTarget,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out uint target) ||
                target is < 1 or > 65535)
            {
                error = "Service Target Port는 1~65535 범위의 숫자여야 합니다.";
                return false;
            }

            uint? published = null;
            string normalizedPublished = publishedPort?.Trim() ?? string.Empty;
            if (normalizedPublished.Length > 0)
            {
                if (!uint.TryParse(
                        normalizedPublished,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out uint parsedPublished) ||
                    parsedPublished is < 1 or > 65535)
                {
                    error = "Public Port는 비워 두거나 1~65535 범위의 숫자를 입력해야 합니다.";
                    return false;
                }

                published = parsedPublished;
            }

            if (!Enum.TryParse(protocol?.Trim(), true, out SwarmPortProtocol parsedProtocol))
            {
                error = "Protocol은 tcp, udp 또는 sctp여야 합니다.";
                return false;
            }

            if (!Enum.TryParse(publishMode?.Trim(), true, out SwarmPublishMode parsedPublishMode))
            {
                error = "Publish Mode는 ingress 또는 host여야 합니다.";
                return false;
            }

            options = new SwarmPublishedPortOptions(
                target,
                published,
                parsedProtocol,
                parsedPublishMode);
            return true;
        }
    }
}
