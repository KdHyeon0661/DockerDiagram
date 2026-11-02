using System.Net;
using Docker.DotNet;

namespace DockerDiagram.Infrastructure
{
    public static class DockerErrorClassifier
    {
        public static bool IsConflict(Exception exception) =>
            HasStatusCode(exception, HttpStatusCode.Conflict) ||
            MessageContains(exception, "already exists", "address already in use", "already connected", "이미 연결");

        public static bool IsNotFound(Exception exception) =>
            HasStatusCode(exception, HttpStatusCode.NotFound) ||
            MessageContains(exception, "no such", "notfound", "not found", "404");

        public static bool IsNotConnected(Exception exception) =>
            IsNotFound(exception) ||
            MessageContains(exception, "is not connected", "연결되어 있지");

        public static bool IsSwarmVersionConflict(Exception exception, string? engineMessage = null) =>
            IsConflict(exception) ||
            (!string.IsNullOrWhiteSpace(engineMessage) &&
             engineMessage.Contains("update out of sequence", StringComparison.OrdinalIgnoreCase));

        private static bool HasStatusCode(Exception exception, HttpStatusCode statusCode)
        {
            for (Exception? current = exception; current != null; current = current.InnerException)
            {
                if (current is DockerApiException dockerException && dockerException.StatusCode == statusCode)
                    return true;
            }

            return false;
        }

        private static bool MessageContains(Exception exception, params string[] fragments)
        {
            for (Exception? current = exception; current != null; current = current.InnerException)
            {
                string message = current.Message ?? string.Empty;
                if (fragments.Any(fragment => message.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }

            return false;
        }
    }
}
