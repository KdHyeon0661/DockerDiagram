using System;
using System.Collections.Generic;
using System.Linq;

namespace DockerDiagram.Common
{
    public enum KeyValueTextErrorKind
    {
        InvalidEntry,
        DuplicateKey
    }

    public sealed record KeyValueTextError(
        KeyValueTextErrorKind Kind,
        string Entry,
        string Key);

    /// <summary>
    /// 여러 줄의 key=value 입력을 공통 규칙으로 분리하고 검증합니다.
    /// 화면별 오류 문구는 호출자가 결정합니다.
    /// </summary>
    public static class KeyValueTextParser
    {
        private static readonly char[] DefaultEntrySeparators = ['\r', '\n'];

        public static IReadOnlyList<string> ReadEntries(
            string? text,
            char[]? entrySeparators = null,
            bool ignoreCommentLines = false)
        {
            IEnumerable<string> entries = (text ?? string.Empty)
                .Split(entrySeparators ?? DefaultEntrySeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Trim())
                .Where(entry => entry.Length > 0);

            if (ignoreCommentLines)
                entries = entries.Where(entry => !entry.StartsWith('#'));

            return entries.ToArray();
        }

        public static bool TrySplitEntry(
            string entry,
            out string key,
            out string value,
            bool requireSeparator = true)
        {
            string normalized = entry?.Trim() ?? string.Empty;
            int separator = normalized.IndexOf('=');

            if (separator < 0)
            {
                key = normalized;
                value = string.Empty;
                return !requireSeparator && key.Length > 0;
            }

            key = normalized[..separator].Trim();
            value = normalized[(separator + 1)..].Trim();
            return key.Length > 0;
        }

        public static bool TryParse(
            string? text,
            out Dictionary<string, string> values,
            out KeyValueTextError? error,
            StringComparer? comparer = null,
            bool requireSeparator = true,
            bool rejectDuplicateKeys = false,
            bool ignoreCommentLines = false,
            Func<string, bool>? keyValidator = null,
            char[]? entrySeparators = null)
        {
            values = new Dictionary<string, string>(comparer ?? StringComparer.Ordinal);

            foreach (string entry in ReadEntries(text, entrySeparators, ignoreCommentLines))
            {
                if (!TrySplitEntry(entry, out string key, out string value, requireSeparator) ||
                    (keyValidator != null && !keyValidator(key)))
                {
                    error = new KeyValueTextError(KeyValueTextErrorKind.InvalidEntry, entry, key);
                    return false;
                }

                if (rejectDuplicateKeys && values.ContainsKey(key))
                {
                    error = new KeyValueTextError(KeyValueTextErrorKind.DuplicateKey, entry, key);
                    return false;
                }

                values[key] = value;
            }

            error = null;
            return true;
        }

        public static string Format(IEnumerable<KeyValuePair<string, string>>? values) =>
            values == null
                ? string.Empty
                : string.Join(Environment.NewLine, values.Select(pair => $"{pair.Key}={pair.Value}"));
    }
}
