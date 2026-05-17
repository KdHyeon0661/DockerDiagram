using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DockerDiagram.Helpers
{
    public class TunnelInfo
    {
        public required Process Process { get; set; }
        public int LocalPort { get; set; }
        public int ReferenceCount { get; set; }
    }

    public static class SshTunnelManager
    {
        private static readonly ConcurrentDictionary<string, TunnelInfo> _activeTunnels = new();
        private static readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        static SshTunnelManager()
        {
            AppDomain.CurrentDomain.ProcessExit += (s, e) => CloseAllTunnels();
        }

        public static async Task<int> GetOrStartTunnelAsync(string hostIp, int sshPort, string username, string keyFilePath, IDialogService dialogService)
        {
            string connectionKey = $"{username}@{hostIp}:{sshPort}";

            await _lock.WaitAsync();
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
                    Arguments = $"-i \"{keyFilePath}\" -N -L {localPort}:/var/run/docker.sock {username}@{hostIp} -p {sshPort}",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                var process = Process.Start(startInfo);
                if (process == null) throw new Exception("SSH 프로세스를 시작할 수 없습니다.");

                var tcs = new TaskCompletionSource<bool>();
                bool isPrompting = false; // ★ 사용자가 응답 대기 중인지 체크하는 플래그

                _ = Task.Run(async () =>
                {
                    try
                    {
                        char[] buffer = new char[1024];
                        StringBuilder sb = new StringBuilder();

                        while (!process.StandardError.EndOfStream)
                        {
                            int bytesRead = await process.StandardError.ReadAsync(buffer, 0, buffer.Length);
                            if (bytesRead > 0)
                            {
                                string text = new string(buffer, 0, bytesRead);
                                sb.Append(text);
                                string currentOutput = sb.ToString();

                                // 1. 호스트 키 검증 프롬프트 포착
                                if (currentOutput.Contains("yes/no") || currentOutput.Contains("continue connecting"))
                                {
                                    isPrompting = true; // ★ 타이머 일시 정지

                                    // 비동기 Invoke로 UI 스레드 데드락 완벽 방지
                                    bool isTrusted = await Application.Current.Dispatcher.InvokeAsync(() =>
                                        dialogService.ShowHostKeyConfirm(hostIp, currentOutput.Trim())
                                    );

                                    if (isTrusted)
                                    {
                                        process.StandardInput.WriteLine("yes");
                                        process.StandardInput.Flush();
                                        sb.Clear();
                                        isPrompting = false; // ★ 타이머 재개
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

                // ★ 개선된 타이머 로직: 사용자가 창을 띄우고 있는 시간은 카운트하지 않음
                int waitTimeMs = 0;
                while (waitTimeMs < 3000) // 3초간 프로세스 생존 및 예외 발생 여부 검증
                {
                    if (tcs.Task.IsFaulted) throw tcs.Task.Exception!.InnerException!;
                    if (process.HasExited) throw new Exception("SSH 연결이 거부되었거나 터널 생성에 실패했습니다. (키 파일 오류 또는 권한 문제)");

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
                    ReferenceCount = 1
                };

                _activeTunnels.TryAdd(connectionKey, newTunnel);
                return localPort;
            }
            finally
            {
                _lock.Release();
            }
        }

        public static void ReleaseTunnel(string hostIp, int sshPort, string username)
        {
            string connectionKey = $"{username}@{hostIp}:{sshPort}";
            _lock.Wait();
            try
            {
                if (_activeTunnels.TryGetValue(connectionKey, out var tunnel))
                {
                    tunnel.ReferenceCount--;
                    if (tunnel.ReferenceCount <= 0)
                    {
                        if (!tunnel.Process.HasExited)
                        {
                            tunnel.Process.Kill();
                            tunnel.Process.Dispose();
                        }
                        _activeTunnels.TryRemove(connectionKey, out _);
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
                    if (!kvp.Value.Process.HasExited)
                    {
                        kvp.Value.Process.Kill();
                        kvp.Value.Process.Dispose();
                    }
                }
                catch { }
            }
            _activeTunnels.Clear();
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