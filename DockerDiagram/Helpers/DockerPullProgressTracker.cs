using Docker.DotNet.Models;

namespace DockerDiagram.Helpers
{
    public sealed class DockerPullProgressSnapshot
    {
        public string Message { get; init; } = "Preparing...";
        public double? Percent { get; init; }
    }

    public sealed class DockerPullProgressTracker
    {
        private readonly Dictionary<string, (long Current, long Total)> _layers = new(StringComparer.OrdinalIgnoreCase);

        public DockerPullProgressSnapshot Update(JSONMessage message)
        {
            string status = message.ErrorMessage
                ?? message.Status
                ?? (string.IsNullOrWhiteSpace(message.Stream) ? "Pulling image" : message.Stream.Trim());

            if (!string.IsNullOrWhiteSpace(message.ID) &&
                message.Progress != null &&
                message.Progress.Total > 0)
            {
                _layers[message.ID] = (message.Progress.Current, message.Progress.Total);
            }

            double? percent = CalculatePercent();
            string detail = string.IsNullOrWhiteSpace(message.ProgressMessage)
                ? string.Empty
                : $" {message.ProgressMessage}";
            string layer = string.IsNullOrWhiteSpace(message.ID)
                ? string.Empty
                : $" [{message.ID}]";
            string percentText = percent.HasValue
                ? $" ({percent.Value:0}%)"
                : string.Empty;

            return new DockerPullProgressSnapshot
            {
                Message = $"{status}{layer}{detail}{percentText}".Trim(),
                Percent = percent
            };
        }

        private double? CalculatePercent()
        {
            long total = _layers.Values.Sum(layer => layer.Total);
            if (total <= 0)
                return null;

            long current = _layers.Values.Sum(layer => Math.Min(layer.Current, layer.Total));
            return Math.Clamp((double)current / total * 100.0, 0.0, 100.0);
        }
    }
}
