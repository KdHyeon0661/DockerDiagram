using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace DockerDiagram.Helpers
{
    /// <summary>
    /// 원격 서버의 도커 데몬(/var/run/docker.sock)과 로컬 PC를 연결하는 SSH 터널의 상태와 참조 횟수를 관리하는 데이터 클래스입니다.
    /// </summary>
    public class TunnelInfo
    {
        public required Process Process { get; set; } // 실행 중인 백그라운드 ssh.exe 프로세스
        public int LocalPort { get; set; } // 로컬 PC에 뚫어놓은 임의의 포트 번호 (예: 23750)
        public int ReferenceCount { get; set; } // 현재 몇 개의 시트(탭)가 이 터널을 공유해서 쓰고 있는지 나타내는 카운트
    }

    /// <summary>
    /// 원격 도커 엔진 제어를 위해 백그라운드에서 SSH 포트 포워딩(터널링) 프로세스를 생성하고 생명주기를 관리하는 정적 서비스 클래스입니다.
    /// 메모리 누수와 프로세스 중복을 막기 위해 참조 카운트(Reference Counting) 패턴을 사용합니다.
    /// </summary>
    public static class SshTunnelManager
    {
        // 현재 활성화되어 있는 모든 터널을 관리하는 딕셔너리 (Key: "ubuntu@192.168.0.10:22")
        private static readonly Dictionary<string, TunnelInfo> _activeTunnels = new();

        /// <summary>
        /// 특정 서버로 향하는 터널이 이미 있다면 기존 포트를 반환하여 재사용하고, 없다면 새로운 터널을 뚫어 포트 번호를 반환합니다.
        /// </summary>
        public static async Task<int> GetOrStartTunnelAsync(string hostIp, int sshPort, string username, string keyFilePath)
        {
            string connectionKey = $"{username}@{hostIp}:{sshPort}"; // 고유 식별키 생성

            // 1. 같은 서버에 이미 뚫려있는 터널이 있다면 프로세스 재사용 (자원 절약)
            if (_activeTunnels.ContainsKey(connectionKey))
            {
                _activeTunnels[connectionKey].ReferenceCount++; // 나도 쓸게! 하고 카운트 증가
                Debug.WriteLine($"[SSH Tunnel] 기존 터널 재사용: {connectionKey} (Port: {_activeTunnels[connectionKey].LocalPort})");
                return _activeTunnels[connectionKey].LocalPort;
            }

            // 2. 뚫려있는 터널이 없다면 비어있는 로컬 포트를 찾아서 새로 개통
            int localPort = GetAvailablePort(23750); // 23750번부터 사용 가능한 빈 포트를 탐색

            var startInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                // 원격지의 docker.sock 통신을 로컬의 특정 포트로 끌어오는 핵심 명령어
                Arguments = $"-i \"{keyFilePath}\" -N -L {localPort}:/var/run/docker.sock {username}@{hostIp} -p {sshPort} -o StrictHostKeyChecking=no",
                CreateNoWindow = true, // 까만 CMD 창 숨기기
                UseShellExecute = false
            };

            var process = Process.Start(startInfo);

            // 터널이 완전히 개통되고 안정화될 때까지 UI 스레드를 막지 않고 2초간 대기
            await Task.Delay(2000);

            // 프로세스가 켜지자마자 꺼졌다면 접속 실패(비밀번호 틀림, 키 없음, 방화벽 등)로 간주
            if (process == null || process.HasExited)
            {
                throw new Exception("SSH 터널 연결에 실패했습니다.\n(IP, 계정명, 키 파일, 방화벽 등을 확인해 주세요.)");
            }

            // 3. 성공적으로 터널이 열렸다면 관리 목록(딕셔너리)에 새 터널 정보를 등록
            _activeTunnels[connectionKey] = new TunnelInfo
            {
                Process = process,
                LocalPort = localPort,
                ReferenceCount = 1 // 처음 개통했으므로 나 혼자 쓰고 있음
            };

            Debug.WriteLine($"[SSH Tunnel] 새 터널 오픈: {connectionKey} -> LocalPort: {localPort}");
            return localPort;
        }

        /// <summary>
        /// 시트(탭)를 닫거나 연결을 끊을 때 호출하여 터널의 참조 카운트를 줄입니다.
        /// 만약 더 이상 이 터널을 쓰는 탭이 하나도 없다면, 백그라운드 프로세스를 안전하게 폭파(Kill)합니다.
        /// </summary>
        public static void ReleaseTunnel(string hostIp, int sshPort, string username)
        {
            string connectionKey = $"{username}@{hostIp}:{sshPort}";

            if (_activeTunnels.ContainsKey(connectionKey))
            {
                _activeTunnels[connectionKey].ReferenceCount--; // 나 이제 다 썼어! 하고 카운트 차감

                // 이 서버를 바라보는 시트가 0개가 되면 좀비 프로세스를 막기 위해 강제 종료
                if (_activeTunnels[connectionKey].ReferenceCount <= 0)
                {
                    var process = _activeTunnels[connectionKey].Process;
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.Dispose();
                    }
                    _activeTunnels.Remove(connectionKey); // 관리 목록에서도 삭제
                    Debug.WriteLine($"[SSH Tunnel] 더 이상 사용하는 탭이 없어 터널을 닫습니다: {connectionKey}");
                }
            }
        }

        /// <summary>
        /// 프로그램이 완전히 종료(X 버튼 클릭 등)될 때 호출되어,
        /// 메모리에 남아있는 모든 SSH 백그라운드 터널들을 일괄적으로 강제 종료합니다.
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
        /// 지정된 시작 포트(startingPort)부터 1씩 증가시키며, 현재 PC에서 아무도 쓰고 있지 않은(충돌나지 않는) 빈 포트 번호를 찾아 반환합니다.
        /// </summary>
        private static int GetAvailablePort(int startingPort)
        {
            int port = startingPort;
            while (true)
            {
                try
                {
                    // TcpListener를 아주 짧게 Start/Stop 해봄으로써 해당 포트가 사용 중인지 검사
                    var listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    listener.Stop();
                    return port; // 에러가 안 났다면 이 포트는 비어있음!
                }
                catch
                {
                    port++; // 이미 누가 쓰고 있어서 에러가 났다면 다음 번호로 넘어감
                }
            }
        }
    }
}