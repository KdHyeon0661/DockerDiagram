using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Runtime.Versioning;

namespace DockerDiagram.Helpers
{
    [SupportedOSPlatform("windows")] // only for Windows
    public static class DockerServiceHelper
    {
        private const string DOCKER_PROCESS_NAME = "Docker Desktop";
        private const string DOCKER_EXE_NAME = "Docker Desktop.exe";

        // 도커가 실행 중
        public static bool IsDockerRunning()
        {
            var processes = Process.GetProcessesByName(DOCKER_PROCESS_NAME);
            return processes.Length > 0;
        }

        // 도커 실행하기. 안하면 안쓰는 것과 같기에
        public static async Task StartDockerAsync()
        {
            if (IsDockerRunning()) return; // 실행 중이면 취소

            // 동적으로 실행 파일 경로 찾기
            string? dockerPath = GetDockerExecutablePath();

            // 2. 경로를 못 찾았으면 사용자에게 물어보기
            if (string.IsNullOrEmpty(dockerPath) || !File.Exists(dockerPath))
            {
                if (MessageBox.Show("Docker Desktop 실행 파일을 자동으로 찾을 수 없습니다.\n직접 지정하시겠습니까?",
                    "경로 확인 필요", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    var dlg = new OpenFileDialog
                    {
                        Filter = "Executable Files (*.exe)|*.exe",
                        Title = "Docker Desktop.exe 파일을 선택해주세요",
                        FileName = "Docker Desktop.exe"
                    };

                    if (dlg.ShowDialog() == true)
                    {
                        dockerPath = dlg.FileName;
                    }
                    else
                    {
                        return; // 취소함
                    }
                }
                else
                {
                    return; // 실행 취소
                }
            }

            // 실행
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dockerPath,
                    UseShellExecute = true
                });

                // 실행 대기
                await WaitForDockerReadyAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"도커 실행 실패: {ex.Message}");
            }
        }

        // 도커가 준비될 때까지 대기
        private static async Task WaitForDockerReadyAsync()
        {
            // DockerApiService는 같은 네임스페이스(DockerDiagram.Helpers)에 있어 바로 사용 가능
            var api = new DockerApiService();
            int timeoutSeconds = 60; // 최대 60초 대기

            for (int i = 0; i < timeoutSeconds; i++)
            {
                // PingAsync()는 실패 시 false를 반환하도록 이미 구현되어 있음
                if (await api.PingAsync())
                {
                    return; // 연결 성공! 즉시 리턴
                }

                await Task.Delay(1000); // 1초 대기 후 재시도
            }

            throw new TimeoutException("Docker Desktop 실행 시간이 초과되었습니다. (60초)");
        }

        // 설치 경로를 동적으로 찾는 로직
        private static string? GetDockerExecutablePath()
        {
            string? path = null;

            // 레지스트리 Uninstall 정보에서 찾기
            try
            {
                // Docker Desktop의 레지스트리 경로
                string registryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Docker Desktop";
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(registryKey))
                {
                    if (key != null)
                    {
                        object? installLoc = key.GetValue("InstallLocation");
                        if (installLoc != null)
                        {
                            path = Path.Combine(installLoc.ToString()!, DOCKER_EXE_NAME);
                            if (File.Exists(path)) return path;
                        }
                    }
                }
            }
            catch { /* 레지스트리 접근 권한 없음 등 무시 */ }

            // 환경변수 이용
            try
            {
                foreach (var root in new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),     // 보통 C:\Program Files
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),  // 보통 C:\Program Files (x86)
                    Environment.GetEnvironmentVariable("ProgramW6432")                     // 32bit 프로세스에서도 64bit Program Files
                }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var candidate = Path.Combine(root!, "Docker", "Docker", DOCKER_EXE_NAME);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch { }

            // 기본 C드라이브 경로, 최후의 수단
            path = @"C:\Program Files\Docker\Docker\Docker Desktop.exe";
            if (File.Exists(path)) return path;

            return null;
        }
    }
}