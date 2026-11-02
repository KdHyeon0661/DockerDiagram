using System;

namespace DockerDiagram.Common
{
    /// <summary>
    /// Docker가 발급한 해시 ID를 일관된 화면 표시 형식으로 변환합니다.
    /// </summary>
    public static class DockerIdFormatter
    {
        public const int DefaultShortLength = 12;

        public static string Shorten(string? id, int length = DefaultShortLength)
        {
            if (string.IsNullOrWhiteSpace(id)) return string.Empty;
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

            string cleanId = id.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? id["sha256:".Length..]
                : id;

            return cleanId.Length <= length ? cleanId : cleanId[..length];
        }
    }
}
