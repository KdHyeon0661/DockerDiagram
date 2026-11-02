using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DockerDiagram.Common;
using DockerDiagram.Contracts;

namespace DockerDiagram.Infrastructure
{
    public sealed class TunnelInfo
    {
        private string _recentError = string.Empty;

        public required Process Process { get; init; }
        public int LocalPort { get; init; }
        public int ReferenceCount { get; set; }
        public string RecentError
        {
            get => Volatile.Read(ref _recentError);
            set => Volatile.Write(ref _recentError, value ?? string.Empty);
        }

        internal Task? ErrorReaderTask { get; set; }
    }

    public static class SshTunnelManager
    {
        public const string DefaultRemoteDockerSocketPath = "/var/run/docker.sock";
        private static readonly TimeSpan TunnelStartupPollInterval = TimeSpan.FromMilliseconds(100);
        private static readonly ConcurrentDictionary<string, TunnelSlot> ActiveTunnels =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly CancellationTokenSource Shutdown = new();
        private static int _closing;

        static SshTunnelManager()
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => CloseAllTunnels();
        }

        public static async Task<int> GetOrStartTunnelAsync(
            string hostIp,
            int sshPort,
            string username,
            string keyFilePath,
            string? remoteDockerSocketPath,
            IDialogService dialogService,
            CancellationToken cancellationToken = default)
        {
            ValidateConnectionArguments(hostIp, sshPort, username, keyFilePath);
            ArgumentNullException.ThrowIfNull(dialogService);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closing) != 0, typeof(SshTunnelManager));

            string socketPath = NormalizeRemoteDockerSocketPath(remoteDockerSocketPath);
            string connectionKey = BuildConnectionKey(hostIp, sshPort, username, socketPath);
            TunnelSlot slot = ActiveTunnels.GetOrAdd(connectionKey, _ => new TunnelSlot());

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                Shutdown.Token);
            CancellationToken token = linkedCancellation.Token;

            await slot.Gate.WaitAsync(token).ConfigureAwait(false);
            Process? processToCleanup = null;
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _closing) != 0, typeof(SshTunnelManager));

                if (slot.Tunnel is { } existingTunnel)
                {
                    if (IsProcessAlive(existingTunnel.Process))
                    {
                        existingTunnel.ReferenceCount++;
                        return existingTunnel.LocalPort;
                    }

                    slot.Tunnel = null;
                    StopAndDisposeSafely(existingTunnel.Process, "종료된 SSH 터널 정리 실패");
                }

                int localPort = GetAvailablePort(23750);
                ProcessStartInfo startInfo = CreateStartInfo(
                    hostIp,
                    sshPort,
                    username,
                    keyFilePath,
                    socketPath,
                    localPort);

                Process process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("SSH 프로세스를 시작할 수 없습니다.");
                processToCleanup = process;
                slot.PendingProcess = process;

                var startupFailure = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var tunnel = new TunnelInfo
                {
                    Process = process,
                    LocalPort = localPort,
                    ReferenceCount = 1
                };
                int isPrompting = 0;
                tunnel.ErrorReaderTask = MonitorSshErrorsAsync(
                    process,
                    tunnel,
                    startupFailure,
                    dialogService,
                    hostIp,
                    () => Volatile.Write(ref isPrompting, 1),
                    () => Volatile.Write(ref isPrompting, 0),
                    token);

                int elapsedMilliseconds = 0;
                while (elapsedMilliseconds < 3000)
                {
                    token.ThrowIfCancellationRequested();
                    if (startupFailure.Task.IsCompleted)
                        await startupFailure.Task.ConfigureAwait(false);
                    if (process.HasExited)
                        throw new InvalidOperationException(BuildSshFailureMessage(tunnel.RecentError));

                    await Task.Delay(TunnelStartupPollInterval, token).ConfigureAwait(false);
                    if (Volatile.Read(ref isPrompting) == 0)
                        elapsedMilliseconds += (int)TunnelStartupPollInterval.TotalMilliseconds;
                }

                // 이후 stderr 감시 오류가 이미 반환된 시작 작업의 TCS에
                // 관찰되지 않은 예외를 남기지 않도록 시작 단계를 닫습니다.
                if (!startupFailure.TrySetResult())
                    await startupFailure.Task.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref _closing) != 0,
                    typeof(SshTunnelManager));
                slot.Tunnel = tunnel;
                slot.PendingProcess = null;
                processToCleanup = null;
                return localPort;
            }
            catch
            {
                if (slot.Tunnel == null)
                    TryRemoveSlot(connectionKey, slot);
                throw;
            }
            finally
            {
                slot.PendingProcess = null;
                if (processToCleanup != null)
                    StopAndDisposeSafely(processToCleanup, "실패한 SSH 터널 프로세스 정리 실패");
                slot.Gate.Release();
            }
        }

        public static string GetRecentTunnelError(
            string hostIp,
            int sshPort,
            string username,
            string? remoteDockerSocketPath)
        {
            string socketPath = NormalizeRemoteDockerSocketPath(remoteDockerSocketPath);
            string connectionKey = BuildConnectionKey(hostIp, sshPort, username, socketPath);
            return ActiveTunnels.TryGetValue(connectionKey, out TunnelSlot? slot)
                ? slot.Tunnel?.RecentError ?? string.Empty
                : string.Empty;
        }

        /// <summary>
        /// Releases a lease without synchronously waiting on the UI thread.
        /// </summary>
        public static void ReleaseTunnel(
            string hostIp,
            int sshPort,
            string username,
            string? remoteDockerSocketPath)
        {
            string socketPath = NormalizeRemoteDockerSocketPath(remoteDockerSocketPath);
            string connectionKey = BuildConnectionKey(hostIp, sshPort, username, socketPath);
            if (!ActiveTunnels.TryGetValue(connectionKey, out TunnelSlot? slot))
                return;

            Task releaseTask = ReleaseTunnelCoreAsync(connectionKey, slot);
            if (!releaseTask.IsCompletedSuccessfully)
            {
                _ = releaseTask.ContinueWith(
                    task => AppLog.Error(
                        "SSH 터널 참조 해제 중 오류가 발생했습니다.",
                        task.Exception?.GetBaseException() ?? new InvalidOperationException("알 수 없는 SSH 터널 해제 오류")),
                    default,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        public static void CloseAllTunnels()
        {
            if (Interlocked.Exchange(ref _closing, 1) != 0)
                return;

            Shutdown.Cancel();
            KeyValuePair<string, TunnelSlot>[] slots = ActiveTunnels.ToArray();
            ActiveTunnels.Clear();

            foreach ((_, TunnelSlot slot) in slots)
            {
                Process? process = slot.Tunnel?.Process ?? slot.PendingProcess;
                if (process != null)
                    StopAndDisposeSafely(process, "애플리케이션 종료 중 SSH 터널 정리 실패");
                slot.Tunnel = null;
                slot.PendingProcess = null;
            }
        }

        public static string NormalizeRemoteDockerSocketPath(string? remoteDockerSocketPath)
        {
            string socketPath = string.IsNullOrWhiteSpace(remoteDockerSocketPath)
                ? DefaultRemoteDockerSocketPath
                : remoteDockerSocketPath.Trim();

            if (!socketPath.StartsWith('/'))
                throw new ArgumentException("원격 Docker 소켓 경로는 /로 시작하는 절대 경로여야 합니다.");
            if (socketPath.IndexOfAny(['\r', '\n', '\0', ':']) >= 0)
                throw new ArgumentException("원격 Docker 소켓 경로에 사용할 수 없는 문자가 포함되어 있습니다.");

            return socketPath;
        }

        internal static bool IsProcessAlive(Process process)
        {
            try
            {
                return !process.HasExited;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        internal static IReadOnlyList<string> BuildOpenSshArguments(
            string hostIp,
            int sshPort,
            string username,
            string keyFilePath,
            string remoteDockerSocketPath,
            int localPort) =>
        [
            "-i", keyFilePath,
            "-p", sshPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-N",
            "-T",
            "-o", "ExitOnForwardFailure=yes",
            "-o", "ServerAliveInterval=15",
            "-o", "ServerAliveCountMax=2",
            "-L", $"{localPort}:{remoteDockerSocketPath}",
            $"{username}@{hostIp}"
        ];

        private static async Task ReleaseTunnelCoreAsync(string connectionKey, TunnelSlot slot)
        {
            await slot.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                TunnelInfo? tunnel = slot.Tunnel;
                if (tunnel == null)
                    return;

                tunnel.ReferenceCount--;
                if (tunnel.ReferenceCount > 0)
                    return;

                slot.Tunnel = null;
                TryRemoveSlot(connectionKey, slot);
                StopAndDisposeSafely(tunnel.Process, "SSH 터널 프로세스 종료 실패");
            }
            finally
            {
                slot.Gate.Release();
            }
        }

        private static ProcessStartInfo CreateStartInfo(
            string hostIp,
            int sshPort,
            string username,
            string keyFilePath,
            string socketPath,
            int localPort)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = false
            };

            foreach (string argument in BuildOpenSshArguments(
                         hostIp,
                         sshPort,
                         username,
                         keyFilePath,
                         socketPath,
                         localPort))
            {
                startInfo.ArgumentList.Add(argument);
            }

            return startInfo;
        }

        private static async Task MonitorSshErrorsAsync(
            Process process,
            TunnelInfo tunnel,
            TaskCompletionSource startupFailure,
            IDialogService dialogService,
            string hostIp,
            Action promptStarted,
            Action promptCompleted,
            CancellationToken cancellationToken)
        {
            try
            {
                char[] buffer = new char[1024];
                var captured = new StringBuilder();

                while (true)
                {
                    int count = await process.StandardError.ReadAsync(
                        buffer.AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    if (count == 0)
                        return;

                    captured.Append(buffer, 0, count);
                    string output = captured.ToString();
                    tunnel.RecentError = output.Length > 4096 ? output[^4096..] : output;

                    if (output.Contains("yes/no", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("continue connecting", StringComparison.OrdinalIgnoreCase))
                    {
                        promptStarted();
                        try
                        {
                            bool trusted = await dialogService.InvokeOnUiThreadAsync(() =>
                                dialogService.ShowHostKeyConfirm(hostIp, output.Trim()));
                            await process.StandardInput.WriteLineAsync(trusted ? "yes" : "no")
                                .ConfigureAwait(false);
                            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                            captured.Clear();

                            if (!trusted)
                            {
                                StopProcess(process);
                                startupFailure.TrySetException(
                                    new InvalidOperationException("사용자가 안전하지 않은 서버 연결을 취소했습니다."));
                                return;
                            }
                        }
                        finally
                        {
                            promptCompleted();
                        }
                    }
                    else if (output.Contains("password:", StringComparison.OrdinalIgnoreCase) ||
                             output.Contains("passphrase", StringComparison.OrdinalIgnoreCase))
                    {
                        StopProcess(process);
                        startupFailure.TrySetException(new InvalidOperationException(
                            "키 파일 인증에 실패했습니다. 비밀번호 접속은 지원하지 않습니다."));
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                startupFailure.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                if (!startupFailure.TrySetException(ex))
                    AppLog.Warning("실행 중인 SSH 터널의 오류 스트림 감시가 중단되었습니다.", ex);
            }
        }

        private static void StopAndDisposeSafely(Process process, string diagnosticMessage)
        {
            try
            {
                StopProcess(process);
            }
            catch (Exception ex)
            {
                AppLog.Warning(diagnosticMessage, ex);
            }
            finally
            {
                process.Dispose();
            }
        }

        private static void StopProcess(Process process)
        {
            if (process.HasExited)
                return;

            process.Kill(entireProcessTree: true);
            process.WaitForExit(2000);
        }

        private static void ValidateConnectionArguments(
            string hostIp,
            int sshPort,
            string username,
            string keyFilePath)
        {
            if (string.IsNullOrWhiteSpace(hostIp))
                throw new ArgumentException("SSH 호스트가 필요합니다.", nameof(hostIp));
            if (hostIp.StartsWith('-') || hostIp.Any(char.IsWhiteSpace) ||
                hostIp.IndexOfAny(['\r', '\n', '\0', '@']) >= 0)
                throw new ArgumentException("SSH 호스트 형식이 올바르지 않습니다.", nameof(hostIp));
            if (sshPort is < 1 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(sshPort), "SSH 포트는 1~65535 사이여야 합니다.");
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("SSH 사용자 이름이 필요합니다.", nameof(username));
            if (username.StartsWith('-') || username.Any(char.IsWhiteSpace) ||
                username.IndexOfAny(['\r', '\n', '\0', '@']) >= 0)
            {
                throw new ArgumentException("SSH 사용자 이름 형식이 올바르지 않습니다.", nameof(username));
            }
            if (string.IsNullOrWhiteSpace(keyFilePath) || !File.Exists(keyFilePath))
                throw new FileNotFoundException("SSH 키 파일을 찾을 수 없습니다.", keyFilePath);
        }

        private static string BuildConnectionKey(
            string hostIp,
            int sshPort,
            string username,
            string socketPath) =>
            $"{username.Trim()}@{hostIp.Trim()}:{sshPort}|{socketPath}";

        private static bool TryRemoveSlot(string connectionKey, TunnelSlot slot) =>
            ((ICollection<KeyValuePair<string, TunnelSlot>>)ActiveTunnels).Remove(
                new KeyValuePair<string, TunnelSlot>(connectionKey, slot));

        private static string BuildSshFailureMessage(string recentError)
        {
            if (recentError.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                return $"SSH 인증이 거부되었습니다. 계정과 키 파일 권한을 확인해 주세요.\n{recentError.Trim()}";
            if (recentError.Contains("Could not resolve hostname", StringComparison.OrdinalIgnoreCase))
                return $"SSH 호스트 주소를 찾을 수 없습니다.\n{recentError.Trim()}";
            if (recentError.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
                recentError.Contains("Connection timed out", StringComparison.OrdinalIgnoreCase))
            {
                return $"SSH 서버에 연결할 수 없습니다. 호스트와 포트를 확인해 주세요.\n{recentError.Trim()}";
            }

            return string.IsNullOrWhiteSpace(recentError)
                ? "SSH 연결이 거부되었거나 터널 생성에 실패했습니다. 키 파일과 권한을 확인해 주세요."
                : $"SSH 터널 생성에 실패했습니다.\n{recentError.Trim()}";
        }

        private static int GetAvailablePort(int startingPort)
        {
            for (int port = Math.Clamp(startingPort, 1, 65535); port <= 65535; port++)
            {
                try
                {
                    using var listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    return port;
                }
                catch (SocketException)
                {
                }
            }

            throw new InvalidOperationException("SSH 터널에 사용할 수 있는 로컬 포트를 찾지 못했습니다.");
        }

        private sealed class TunnelSlot
        {
            public SemaphoreSlim Gate { get; } = new(1, 1);
            public TunnelInfo? Tunnel { get; set; }
            public Process? PendingProcess { get; set; }
        }
    }
}
