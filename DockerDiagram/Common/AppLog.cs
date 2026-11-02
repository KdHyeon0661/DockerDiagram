using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DockerDiagram.Common
{
    /// <summary>
    /// Records operational failures that would otherwise only be visible under a debugger.
    /// Logging must never interrupt the user operation that produced the diagnostic.
    /// </summary>
    public static class AppLog
    {
        private const long MaximumLogBytes = 2 * 1024 * 1024;
        private static readonly object Sync = new();
        private static readonly ConcurrentDictionary<string, DateTimeOffset> LastWarningTimes =
            new(StringComparer.Ordinal);

        public static string LogDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DockerDiagram",
            "Logs");

        public static string LogFilePath => Path.Combine(LogDirectory, "application.log");

        public static void Info(string message) => Write("INFO", message, null);

        public static void Warning(string message, Exception? exception = null) =>
            Write("WARN", message, exception);

        public static void WarningThrottled(
            string key,
            string message,
            Exception? exception = null,
            TimeSpan? minimumInterval = null)
        {
            TimeSpan interval = minimumInterval ?? TimeSpan.FromMinutes(1);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            while (true)
            {
                if (LastWarningTimes.TryGetValue(key, out DateTimeOffset last) && now - last < interval)
                    return;
                if (LastWarningTimes.TryAdd(key, now) ||
                    LastWarningTimes.TryUpdate(key, now, last))
                {
                    Warning(message, exception);
                    return;
                }
            }
        }

        public static void Error(string message, Exception exception) =>
            Write("ERROR", message, exception);

        private static void Write(string level, string message, Exception? exception)
        {
            string detail = exception == null
                ? message
                : $"{message}{Environment.NewLine}{exception}";
            string entry = $"{DateTimeOffset.Now:O} [{level}] {detail}";

            Debug.WriteLine(entry);
            Trace.WriteLine(entry);

            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(LogDirectory);
                    RotateIfNeeded();
                    File.AppendAllText(
                        LogFilePath,
                        entry + Environment.NewLine,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
            }
            catch
            {
                // Diagnostics must not replace or hide the original application result.
            }
        }

        private static void RotateIfNeeded()
        {
            if (!File.Exists(LogFilePath) || new FileInfo(LogFilePath).Length < MaximumLogBytes)
                return;

            string previousLogPath = Path.Combine(LogDirectory, "application.previous.log");
            if (File.Exists(previousLogPath))
                File.Delete(previousLogPath);
            File.Move(LogFilePath, previousLogPath);
        }
    }
}
