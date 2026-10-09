using DockerDiagram.Models;
using System.Text.RegularExpressions;

namespace DockerDiagram.ApplicationServices
{
    public static partial class SwarmResourceReferenceInputParser
    {
        public static SwarmResourceTargetOptions Parse(
            string target,
            string uid,
            string gid,
            string mode)
        {
            if (!TryParse(target, uid, gid, mode, out SwarmResourceTargetOptions options, out string error))
                throw new ArgumentException(error);

            return options;
        }

        public static bool TryParse(
            string target,
            string uid,
            string gid,
            string mode,
            out SwarmResourceTargetOptions options,
            out string error)
        {
            options = new SwarmResourceTargetOptions(string.Empty);
            error = string.Empty;

            string normalizedTarget = target?.Trim() ?? string.Empty;
            if (normalizedTarget.Length == 0 || normalizedTarget.IndexOfAny(['\0', '\r', '\n']) >= 0)
            {
                error = "Target은 컨테이너에서 사용할 파일 이름 또는 절대 경로여야 합니다.";
                return false;
            }

            string normalizedUid = uid?.Trim() ?? string.Empty;
            if (!IsValidOwner(normalizedUid))
            {
                error = "UID는 숫자 ID 또는 컨테이너에 존재하는 사용자 이름이어야 합니다.";
                return false;
            }

            string normalizedGid = gid?.Trim() ?? string.Empty;
            if (!IsValidOwner(normalizedGid))
            {
                error = "GID는 숫자 ID 또는 컨테이너에 존재하는 그룹 이름이어야 합니다.";
                return false;
            }

            string normalizedMode = mode?.Trim() ?? string.Empty;
            if (normalizedMode.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
                normalizedMode = normalizedMode[2..];
            if (normalizedMode.Length == 0 || normalizedMode.Length > 4 ||
                normalizedMode.Any(character => character is < '0' or > '7'))
            {
                error = "Mode는 0000~0777 범위의 8진수 권한이어야 합니다. 예: 0444, 0400";
                return false;
            }

            uint parsedMode;
            try
            {
                parsedMode = Convert.ToUInt32(normalizedMode, 8);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                error = "Mode는 0000~0777 범위의 8진수 권한이어야 합니다. 예: 0444, 0400";
                return false;
            }

            if (parsedMode > 0x1FF)
            {
                error = "Mode는 0000~0777 범위의 8진수 권한이어야 합니다. 예: 0444, 0400";
                return false;
            }

            options = new SwarmResourceTargetOptions(
                normalizedTarget,
                normalizedUid,
                normalizedGid,
                parsedMode);
            return true;
        }

        public static string FormatMode(uint mode) =>
            Convert.ToString(mode, 8).PadLeft(4, '0');

        private static bool IsValidOwner(string value) =>
            value.Length > 0 && OwnerPattern().IsMatch(value);

        [GeneratedRegex("^(?:[0-9]+|[A-Za-z_][A-Za-z0-9_.-]*)$", RegexOptions.CultureInvariant)]
        private static partial Regex OwnerPattern();
    }
}
