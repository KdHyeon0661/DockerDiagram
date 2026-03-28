using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace DockerDiagram.Helpers
{
    // 현재 열려있는 터널의 정보를 담는 클래스
    public class TunnelInfo
    {
        public Process Process { get; set; }
        public int LocalPort { get; set; }
        public int ReferenceCount { get; set; } // 몇 개의 시트(탭)가 이 터널을 쓰고 있는지 카운트
    }

    public static class SshTunnelManager
    {
        // 딕셔너리로 열려있는 터널들을 관리합니다. (키: "ubuntu@192.168.0.10:22")
        private static readonly Dictionary<string, TunnelInfo> _activeTunnels = new();

        /// <summary>
        /// 터널을 열거나, 이미 열려있다면 기존 포트 번호를 반환합니다.
        /// </summary>
        public static async Task<int> GetOrStartTunnelAsync(string hostIp, int sshPort, string username, string keyFilePath)
        {
            string connectionKey = $"{username}@{hostIp}:{sshPort}";

            // 1. 같은 서버에 이미 뚫려있는 터널이 있다면 재사용! (참조 카운트만 +1 증가)
            if (_activeTunnels.ContainsKey(connectionKey))
            {
                _activeTunnels[connectionKey].ReferenceCount++;
                Debug.WriteLine($"[SSH Tunnel] 기존 터널 재사용: {connectionKey} (Port: {_activeTunnels[connectionKey].LocalPort})");
                return _activeTunnels[connectionKey].LocalPort;
            }

            // 2. 없다면 비어있는 포트를 찾아서 새로 뚫기
            int localPort = GetAvailablePort(23750);

            var startInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                Arguments = $"-i \"{keyFilePath}\" -N -L {localPort}:/var/run/docker.sock {username}@{hostIp} -p {sshPort} -o StrictHostKeyChecking=no",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            var process = Process.Start(startInfo);

            // 터널 안정화 대기 (UI가 멈추지 않는 비동기 대기)
            await Task.Delay(2000);

            if (process == null || process.HasExited)
            {
                throw new Exception("SSH 터널 연결에 실패했습니다.\n(IP, 계정명, 키 파일, 방화벽 등을 확인해 주세요.)");
            }

            // 3. 딕셔너리에 새 터널 등록 (최초 참조 카운트는 1)
            _activeTunnels[connectionKey] = new TunnelInfo
            {
                Process = process,
                LocalPort = localPort,
                ReferenceCount = 1
            };

            Debug.WriteLine($"[SSH Tunnel] 새 터널 오픈: {connectionKey} -> LocalPort: {localPort}");
            return localPort;
        }

        /// <summary>
        /// 시트(탭)를 닫을 때 호출합니다. 아무도 안 쓰면 터널을 폭파합니다.
        /// </summary>
        public static void ReleaseTunnel(string hostIp, int sshPort, string username)
        {
            string connectionKey = $"{username}@{hostIp}:{sshPort}";

            if (_activeTunnels.ContainsKey(connectionKey))
            {
                _activeTunnels[connectionKey].ReferenceCount--;

                // 이 서버를 바라보는 시트가 0개가 되면 프로세스 종료
                if (_activeTunnels[connectionKey].ReferenceCount <= 0)
                {
                    var process = _activeTunnels[connectionKey].Process;
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.Dispose();
                    }
                    _activeTunnels.Remove(connectionKey);
                    Debug.WriteLine($"[SSH Tunnel] 더 이상 사용하는 탭이 없어 터널을 닫습니다: {connectionKey}");
                }
            }
        }

        /// <summary>
        /// 프로그램이 완전히 종료될 때 모든 터널을 안전하게 닫습니다.
        /// </summary>
        public static void CloseAllTunnels()
        {
            foreach (var tunnel in _activeTunnels.Values)
            {
                if (!tunnel.Process.HasExited)
                {
                    tunnel.Process.Kill();
                    tunnel.Process.Dispose();
                }
            }
            _activeTunnels.Clear();
            Debug.WriteLine("[SSH Tunnel] 모든 터널 강제 종료 완료.");
        }

        /// <summary>
        /// 사용 중이지 않은 빈 포트를 자동으로 찾아주는 헬퍼
        /// </summary>
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
                    port++; // 사용 중이면 다음 번호로 넘어감
                }
            }
        }
    }
}