using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DockerDiagram.Infrastructure
{
    /// <summary>
    /// 다이어그램에 포함될 수 있는 환경 변수와 원본 매니페스트를 현재 Windows 사용자로 보호합니다.
    /// 접두사가 없는 값은 이전 버전의 평문 파일로 간주해 그대로 읽습니다.
    /// </summary>
    internal static class UserDataProtection
    {
        internal const string Prefix = "dpapi:v1:";
        private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("DockerDiagram.vdm.sensitive-data.v1");

        public static string Protect(string? value)
        {
            if (string.IsNullOrEmpty(value) || value.StartsWith(Prefix, StringComparison.Ordinal))
                return value ?? string.Empty;

            byte[] plainBytes = Encoding.UTF8.GetBytes(value);
            byte[] protectedBytes = ProtectedData.Protect(
                plainBytes,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);

            return Prefix + Convert.ToBase64String(protectedBytes);
        }

        public static string Unprotect(string? value)
        {
            if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
                return value ?? string.Empty;

            try
            {
                byte[] protectedBytes = Convert.FromBase64String(value[Prefix.Length..]);
                byte[] plainBytes = ProtectedData.Unprotect(
                    protectedBytes,
                    OptionalEntropy,
                    DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                throw new InvalidDataException(
                    "이 파일의 보호된 데이터를 해독할 수 없습니다. 파일을 저장한 Windows 사용자 계정에서 열어 주세요.",
                    ex);
            }
        }

        public static List<string> ProtectAll(IEnumerable<string>? values) =>
            values?.Select(Protect).ToList() ?? [];

        public static List<string> UnprotectAll(IEnumerable<string>? values) =>
            values?.Select(Unprotect).ToList() ?? [];
    }
}
