using System.Globalization;
using System.Text.RegularExpressions;
using Docker.DotNet.Models;
using DockerDiagram.Common;

namespace DockerDiagram.Infrastructure
{
    public sealed class DockerPullProgressSnapshot
    {
        public string Message { get; init; } = "이미지 다운로드 준비 중";
        public string DetailMessage { get; init; } = "Docker에서 이미지 레이어 정보를 기다리고 있습니다.";
        public double? Percent { get; init; }
        public double? LargestLayerPercent { get; init; }
        public double? DisplayPercent => LargestLayerPercent ?? Percent;
        public long CurrentBytes { get; init; }
        public long TotalBytes { get; init; }
        public long LargestLayerCurrentBytes { get; init; }
        public long LargestLayerTotalBytes { get; init; }
        public int ObservedLayerCount { get; init; }
    }

    public sealed class DockerPullProgressTracker
    {
        private readonly LayerProgressAccumulator _layers = new();
        private readonly string _operationLabel;

        public DockerPullProgressTracker(string operationLabel = "이미지 다운로드")
        {
            _operationLabel = string.IsNullOrWhiteSpace(operationLabel)
                ? "이미지 다운로드"
                : operationLabel.Trim();
        }

        public DockerPullProgressSnapshot Update(JSONMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            string status = NormalizeStatus(
                message.ErrorMessage
                ?? message.Status
                ?? (string.IsNullOrWhiteSpace(message.Stream) ? string.Empty : message.Stream));
            string layerId = message.ID?.Trim() ?? string.Empty;
            _layers.Observe(layerId);

            if (!string.IsNullOrWhiteSpace(layerId) &&
                message.Progress != null &&
                message.Progress.Total > 0 &&
                (status.Contains("Downloading", StringComparison.OrdinalIgnoreCase) ||
                 !_layers.HasMeasuredLayer(layerId)))
            {
                _layers.Update(layerId, message.Progress.Current, message.Progress.Total);
            }

            if (!string.IsNullOrWhiteSpace(layerId) &&
                (status.Contains("Pull complete", StringComparison.OrdinalIgnoreCase) ||
                 status.Contains("Already exists", StringComparison.OrdinalIgnoreCase)))
            {
                _layers.Complete(layerId);
            }

            return _layers.CreateSnapshot(
                _operationLabel,
                status,
                layerId,
                hasError: !string.IsNullOrWhiteSpace(message.ErrorMessage));
        }

        private static string NormalizeStatus(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string singleLine = Regex.Replace(value, @"\s+", " ").Trim();
            return singleLine.Length <= 160 ? singleLine : singleLine[..157] + "...";
        }
    }

    /// <summary>
    /// docker run이 표준 오류로 내보내는 진행 문자열에서 바이트 비율을 읽습니다.
    /// Docker CLI/엔진 버전에 따라 바이트가 생략되면 퍼센트 없이 현재 단계만 반환합니다.
    /// </summary>
    public sealed class DockerCliPullProgressTracker
    {
        private static readonly Regex LayerLineRegex = new(
            @"^\s*(?<id>[0-9a-f]{6,64}):\s*(?<status>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ByteRatioRegex = new(
            @"(?<current>\d+(?:\.\d+)?)\s*(?<currentUnit>[kmgt]?i?b)\s*/\s*(?<total>\d+(?:\.\d+)?)\s*(?<totalUnit>[kmgt]?i?b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AnsiRegex = new(
            "\u001B\\[[0-?]*[ -/]*[@-~]",
            RegexOptions.Compiled);

        private readonly LayerProgressAccumulator _layers = new();

        public DockerPullProgressSnapshot Update(string outputLine)
        {
            string cleanLine = AnsiRegex.Replace(outputLine ?? string.Empty, string.Empty).Trim();
            Match layerMatch = LayerLineRegex.Match(cleanLine);
            string layerId = layerMatch.Success ? layerMatch.Groups["id"].Value : string.Empty;
            string status = layerMatch.Success ? layerMatch.Groups["status"].Value.Trim() : cleanLine;
            _layers.Observe(layerId);

            Match ratioMatch = ByteRatioRegex.Match(status);
            if (!string.IsNullOrWhiteSpace(layerId) && ratioMatch.Success &&
                TryParseBytes(ratioMatch.Groups["current"].Value, ratioMatch.Groups["currentUnit"].Value, out long current) &&
                TryParseBytes(ratioMatch.Groups["total"].Value, ratioMatch.Groups["totalUnit"].Value, out long total) &&
                total > 0)
            {
                _layers.Update(layerId, current, total);
            }

            if (!string.IsNullOrWhiteSpace(layerId) &&
                (status.Contains("Pull complete", StringComparison.OrdinalIgnoreCase) ||
                 status.Contains("Already exists", StringComparison.OrdinalIgnoreCase)))
            {
                _layers.Complete(layerId);
            }

            return _layers.CreateSnapshot("Docker CLI 이미지 다운로드", status, layerId, hasError: false);
        }

        private static bool TryParseBytes(string numberText, string unitText, out long bytes)
        {
            bytes = 0;
            if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                value < 0)
            {
                return false;
            }

            string normalizedUnit = unitText.Trim().ToUpperInvariant();
            double multiplier = normalizedUnit switch
            {
                "KB" => 1_000d,
                "KIB" => 1_024d,
                "MB" => 1_000_000d,
                "MIB" => 1_048_576d,
                "GB" => 1_000_000_000d,
                "GIB" => 1_073_741_824d,
                "TB" => 1_000_000_000_000d,
                "TIB" => 1_099_511_627_776d,
                _ => 1d
            };
            double converted = value * multiplier;
            if (converted > long.MaxValue)
                return false;

            bytes = (long)Math.Round(converted, MidpointRounding.AwayFromZero);
            return true;
        }
    }

    internal sealed class LayerProgressAccumulator
    {
        private readonly Dictionary<string, (long Current, long Total)> _layers =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _observedLayerIds = new(StringComparer.OrdinalIgnoreCase);

        public void Observe(string layerId)
        {
            if (!string.IsNullOrWhiteSpace(layerId))
                _observedLayerIds.Add(layerId);
        }

        public bool HasMeasuredLayer(string layerId) => _layers.ContainsKey(layerId);

        public void Update(string layerId, long current, long total)
        {
            if (string.IsNullOrWhiteSpace(layerId) || total <= 0)
                return;

            _observedLayerIds.Add(layerId);
            _layers[layerId] = (Math.Clamp(current, 0, total), total);
        }

        public void Complete(string layerId)
        {
            if (_layers.TryGetValue(layerId, out var progress))
                _layers[layerId] = (progress.Total, progress.Total);
        }

        public DockerPullProgressSnapshot CreateSnapshot(
            string operationLabel,
            string status,
            string currentLayerId,
            bool hasError)
        {
            long total = _layers.Values.Sum(layer => layer.Total);
            long current = _layers.Values.Sum(layer => Math.Min(layer.Current, layer.Total));
            double? percent = total > 0
                ? Math.Clamp((double)current / total * 100.0, 0.0, 100.0)
                : null;

            (long Current, long Total) largest = _layers.Count == 0
                ? default
                : _layers.Values.MaxBy(layer => layer.Total);
            double? largestPercent = largest.Total > 0
                ? Math.Clamp((double)largest.Current / largest.Total * 100.0, 0.0, 100.0)
                : null;

            double? displayPercent = largestPercent ?? percent;
            string compact = hasError
                ? $"{operationLabel} 오류"
                : displayPercent.HasValue
                    ? $"{operationLabel} {displayPercent.Value:0}%"
                    : $"{operationLabel} 중";

            var details = new List<string>();
            if (total > 0)
                details.Add($"전체 {ByteSizeFormatter.FormatIec(current)} / {ByteSizeFormatter.FormatIec(total)}");
            if (largest.Total > 0)
            {
                details.Add(
                    $"최대 레이어 {ByteSizeFormatter.FormatIec(largest.Current)} / {ByteSizeFormatter.FormatIec(largest.Total)} ({largestPercent:0}%)");
            }
            if (_observedLayerIds.Count > 0)
                details.Add($"레이어 {_observedLayerIds.Count}개 확인");
            if (!string.IsNullOrWhiteSpace(status))
            {
                string layerSuffix = string.IsNullOrWhiteSpace(currentLayerId)
                    ? string.Empty
                    : $" [{ShortenLayerId(currentLayerId)}]";
                details.Add(status + layerSuffix);
            }

            return new DockerPullProgressSnapshot
            {
                Message = compact,
                DetailMessage = details.Count == 0
                    ? "Docker에서 이미지 레이어 정보를 기다리고 있습니다."
                    : string.Join(" · ", details),
                Percent = percent,
                LargestLayerPercent = largestPercent,
                CurrentBytes = current,
                TotalBytes = total,
                LargestLayerCurrentBytes = largest.Current,
                LargestLayerTotalBytes = largest.Total,
                ObservedLayerCount = _observedLayerIds.Count
            };
        }

        private static string ShortenLayerId(string value) =>
            DockerIdFormatter.Shorten(value);

    }
}
