using DockerDiagram.Contracts;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DockerDiagram.Infrastructure
{
    public class TunnelInfo
    {
        public required Process Process { get; set; }
        public int LocalPort { get; set; }
        public int ReferenceCount { get; set; }
        public string RecentError { get; set; } = "";
    }

    public static class SshTunnelManager
    {
        public const string DefaultRemoteDockerSocketPath = "/var/run/docker.sock";
        private static readonly ConcurrentDictionary<string, TunnelInfo> _activeTunnels = new();
        private static readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        static SshTunnelManager()
        {
            AppDomain.CurrentDomain.ProcessExit += (s, e) => CloseAllTunnels();
        }

        public static async Task<int> GetOrStartTunnelAsync(string hostIp, int sshPort, string username, string keyFilePath, string? remoteDockerSocketPath, IDialogService dialogService)
        {
            string socketPath = NormalizeRemoteDockerSocketPath(remoteDockerSocketPath);
            string connectionKey = BuildConnectionKey(hostIp, sshPort, username, socketPath);

            await _lock.WaitAsync();
            Process? processToCleanup = null;
            try
            {
                if (_activeTunnels.TryGetValue(connectionKey, out var existingTunnel))
                {
                    existingTunnel.ReferenceCount++;
                    return existingTunnel.LocalPort;
                }

                int localPort = GetAvailablePort(23750);

                var startInfo = new ProcessStartInfo
                {
                    FileName = "ssh",

                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                startInfo.ArgumentList.Add("-i");
                startInfo.ArgumentList.Add(keyFilePath);
                startInfo.ArgumentList.Add("-N");
                startInfo.ArgumentList.Add("-L");
                startInfo.ArgumentList.Add($"{localPort}:{socketPath}");
                startInfo.ArgumentList.Add($"{username}@{hostIp}");
                startInfo.ArgumentList.Add("-p");
                startInfo.ArgumentList.Add(sshPort.ToString());

                var process = Process.Start(startInfo);
                if (process == null) throw new Exception("SSH 프로세스를 시작할 수 없습니다.");
                processToCleanup = process;

                var tcs = new TaskCompletionSource<bool>();
                bool isPrompting = false;
                string recentError = "";
                TunnelInfo? registeredTunnel = null;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        char[] buffer = new char[1024];
                        StringBuilder sb = new StringBuilder();

                        while (true)
                        {
                            int bytesRead = await process.StandardError.ReadAsync(buffer, 0, buffer.Length);
                            if (bytesRead == 0) break;

                            if (bytesRead > 0)
                            {
                                string text = new string(buffer, 0, bytesRead);
                                sb.Append(text);
                                string currentOutput = sb.ToString();
                                recentError = currentOutput.Length > 4096 ? currentOutput[^4096..] : currentOutput;
                                if (registeredTunnel != null) registeredTunnel.RecentError = recentError;

                                // 1. 호스트 키 검증 프롬프트 포착
                                if (currentOutput.Contains("yes/no") || currentOutput.Contains("continue connecting"))
                                {
                                    isPrompting = true;

                                    // 호스트 키 확인은 UI 스레드에서 실행합니다.
                                    bool isTrusted = await dialogService.InvokeOnUiThreadAsync(() =>
                                        dialogService.ShowHostKeyConfirm(hostIp, currentOutput.Trim())
                                    );

                                    if (isTrusted)
                                    {
                                        process.StandardInput.WriteLine("yes");
                                        process.StandardInput.Flush();
                                        sb.Clear();
                                        isPrompting = false;
                                    }
                                    else
                                    {
                                        process.StandardInput.WriteLine("no");
                                        process.StandardInput.Flush();
                                        process.Kill();
                                        tcs.TrySetException(new Exception("사용자가 안전하지 않은 서버 연결을 취소했습니다."));
                                        return;
                                    }
                                }
                                // 2. 키 파일 오류로 비밀번호를 묻는 경우 (Hang 방지)
                                else if (currentOutput.Contains("password:") || currentOutput.Contains("passphrase"))
                                {
                                    process.Kill();
                                    tcs.TrySetException(new Exception("키 파일(.pem) 인증에 실패했습니다.\n비밀번호 접속은 지원하지 않습니다. 키 파일을 확인해 주세요."));
                                    return;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                });

                // 사용자 확인창이 열린 동안에는 연결 시간 제한을 진행하지 않습니다.
                int waitTimeMs = 0;
                while (waitTimeMs < 3000) // 3초간 프로세스 생존 및 예외 발생 여부 검증
                {
                    if (tcs.Task.IsFaulted) throw tcs.Task.Exception!.InnerException!;
                    if (process.HasExited) throw new Exception(BuildSshFailureMessage(recentError));

                    await Task.Delay(100);

                    // 다이얼로그가 떠있지 않을 때만 타이머를 흘려보냄
                    if (!isPrompting)
                    {
                        waitTimeMs += 100;
                    }
                }

                var newTunnel = new TunnelInfo
                {
                    Process = process,
                    LocalPort = localPort,
                    ReferenceCount = 1,
                    RecentError = recentError
                };
                registeredTunnel = newTunnel;

                if (!_activeTunnels.TryAdd(connectionKey, newTunnel))
                    throw new InvalidOperationException("SSH 터널 등록에 실패했습니다.");

                processToCleanup = null;
                return localPort;
            }
            finally
            {
                if (processToCleanup != null)
                {
                    try { StopAndDispose(processToCleanup); }
                    catch { }
                }

                _lock.Release();
            }
        }

        public static string GetRecentTunnelError(string hostIp, int sshPort, string username, string? remoteDockerSocketPath)
        {
            string socketPath = NormalizeRemoteDockerSocketPath(remoteDockerSocketPath);
            string connectionKey = BuildConnectionKey(hostIp, sshPort, username, socketPath);
            return _activeTunnels.TryGetValue(connectionKey, out var tunnel) ? tunnel.RecentError : "";
        }

        public static void ReleaseTunnel(string hostIp, int sshPort, string username, string? remoteDockerSocketPath)
        {
            string socketPath = NormalizeRemoteDockerSocketPath(remoteDockerSocketPath);
            string connectionKey = BuildConnectionKey(hostIp, sshPort, username, socketPath);
            _lock.Wait();
            try
            {
                if (_activeTunnels.TryGetValue(connectionKey, out var tunnel))
                {
                    tunnel.ReferenceCount--;
                    if (tunnel.ReferenceCount <= 0)
                    {
                        _activeTunnels.TryRemove(connectionKey, out _);
                        StopAndDispose(tunnel.Process);
                    }
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        public static void CloseAllTunnels()
        {
            foreach (var kvp in _activeTunnels)
            {
                try
                {
                    StopAndDispose(kvp.Value.Process);
                }
                catch { }
            }
            _activeTunnels.Clear();
        }

        private static void StopAndDispose(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            finally
            {
                process.Dispose();
            }
        }

        public static string NormalizeRemoteDockerSocketPath(string? remoteDockerSocketPath)
        {
            string socketPath = string.IsNullOrWhiteSpace(remoteDockerSocketPath)
                ? DefaultRemoteDockerSocketPath
                : remoteDockerSocketPath.Trim();

            if (!socketPath.StartsWith('/'))
                throw new ArgumentException("원격 Docker 소켓 경로는 /로 시작하는 절대 경로여야 합니다.");

            if (socketPath.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new ArgumentException("원격 Docker 소켓 경로에 사용할 수 없는 문자가 포함되어 있습니다.");

            return socketPath;
        }

        private static string BuildConnectionKey(string hostIp, int sshPort, string username, string socketPath)
            => $"{username}@{hostIp}:{sshPort}|{socketPath}";

        private static string BuildSshFailureMessage(string recentError)
        {
            if (recentError.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                return $"SSH 인증이 거부되었습니다. 계정과 키 파일 권한을 확인해 주세요.\n{recentError.Trim()}";
            if (recentError.Contains("Could not resolve hostname", StringComparison.OrdinalIgnoreCase))
                return $"SSH 호스트 주소를 찾을 수 없습니다.\n{recentError.Trim()}";
            if (recentError.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
                recentError.Contains("Connection timed out", StringComparison.OrdinalIgnoreCase))
                return $"SSH 서버에 연결할 수 없습니다. 호스트와 포트를 확인해 주세요.\n{recentError.Trim()}";

            return string.IsNullOrWhiteSpace(recentError)
                ? "SSH 연결이 거부되었거나 터널 생성에 실패했습니다. (키 파일 오류 또는 권한 문제)"
                : $"SSH 터널 생성에 실패했습니다.\n{recentError.Trim()}";
        }

        private static int GetAvailablePort(int startingPort)
        {
            int port = startingPort;
            while (true)
            {
                try
                {
                    var listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    listener.Stop();
                    return port;
                }
                catch
                {
                    port++;
                }
            }
        }
    }
}
