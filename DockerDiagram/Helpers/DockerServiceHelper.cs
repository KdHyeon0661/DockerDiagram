using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;

namespace DockerDiagram.Helpers
{
    [SupportedOSPlatform("windows")]
    public static class DockerServiceHelper
    {
        private const string DOCKER_PROCESS_NAME = "Docker Desktop";
        private const string DOCKER_EXE_NAME = "Docker Desktop.exe";

        // 도커가 실행 중인지 확인
        public static bool IsDockerRunning()
        {
            var processes = Process.GetProcessesByName(DOCKER_PROCESS_NAME);
            return processes.Length > 0;
        }

        // ★ [수정] 서비스를 매개변수로 받아야 함
        public static async Task StartDockerAsync(IDockerService dockerService, IDialogService dialogService)
        {
            if (IsDockerRunning()) return; // 실행 중이면 취소

            // 동적으로 실행 파일 경로 찾기
            string? dockerPath = GetDockerExecutablePath();

            // 2. 경로를 못 찾았으면 사용자에게 물어보기
            if (string.IsNullOrEmpty(dockerPath) || !File.Exists(dockerPath))
            {
                // ★ [수정] IDialogService 사용
                bool userWantsToSelect = dialogService.ShowConfirm(
                    "Docker Desktop 실행 파일을 자동으로 찾을 수 없습니다.\n직접 지정하시겠습니까?",
                    "경로 확인 필요");

                if (userWantsToSelect)
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

                // ★ [수정] 실행 대기 시 서비스 전달
                await WaitForDockerReadyAsync(dockerService);
            }
            catch (Exception ex)
            {
                // ★ [수정] IDialogService 사용
                dialogService.ShowMessage($"도커 실행 실패: {ex.Message}");
            }
        }

        // ★ [수정] 도커 서비스를 매개변수로 받음
        private static async Task WaitForDockerReadyAsync(IDockerService dockerService)
        {
            int timeoutSeconds = 60; // 최대 60초 대기

            for (int i = 0; i < timeoutSeconds; i++)
            {
                // ★ [핵심] Instance 대신 주입받은 dockerService 사용
                if (await dockerService.PingAsync())
                {
                    return; // 연결 성공! 즉시 리턴
                }

                await Task.Delay(1000); // 1초 대기 후 재시도
            }

            throw new TimeoutException("Docker Desktop 실행 시간이 초과되었습니다. (60초)");
        }

        // 설치 경로를 동적으로 찾는 로직 (변경 없음)
        private static string? GetDockerExecutablePath()
        {
            string? path = null;

            // 레지스트리 Uninstall 정보에서 찾기
            try
            {
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
            catch (Exception ex)
            {
                Debug.WriteLine($"[DockerDiscovery] 레지스트리 허용 실패 : {ex.Message}");
            }

            // 환경변수 이용
            try
            {
                foreach (var root in new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetEnvironmentVariable("ProgramW6432")
                }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var candidate = Path.Combine(root!, "Docker", "Docker", DOCKER_EXE_NAME);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DockerDiscovery] 환경변수 경로 찾기 실패: {ex.Message}");
            }

            // 기본 C드라이브 경로, 최후의 수단
            path = @"C:\Program Files\Docker\Docker\Docker Desktop.exe";
            if (File.Exists(path)) return path;

            return null;
        }
    }
}