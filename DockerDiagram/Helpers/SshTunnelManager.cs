using System.Diagnostics;

namespace DockerDiagram.Helpers
{
    public class SshTunnelManager
    {
        private Process _sshProcess;

        // SSH 터널 열기 (AWS, Azure 등에서 쓰는 .pem 또는 .ppk 키 파일 방식)
        public bool StartTunnel(string ip, string username, string keyFilePath, int sshPort = 22, int localPort = 23750)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "ssh",
                    // -N: 원격 명령어 실행 안 함 (터널링 전용)
                    // -L: 로컬포트 -> 원격서버의 도커 소켓으로 연결
                    // -o StrictHostKeyChecking=no: 첫 접속 시 y/n 묻는 프롬프트 무시
                    Arguments = $"-i \"{keyFilePath}\" -N -L {localPort}:/var/run/docker.sock {username}@{ip} -p {sshPort} -o StrictHostKeyChecking=no",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };

                _sshProcess = Process.Start(startInfo);

                // 터널이 안정적으로 뚫릴 때까지 1~2초 정도 잠시 대기
                System.Threading.Thread.Sleep(2000);

                return !_sshProcess.HasExited;
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SSH 터널링 실패: {ex.Message}");
                return false;
            }
        }

        // 터널 닫기 (연결 해제 시 호출)
        public void StopTunnel()
        {
            if (_sshProcess != null && !_sshProcess.HasExited)
            {
                _sshProcess.Kill();
                _sshProcess.Dispose();
                _sshProcess = null;
            }
        }
    }
}