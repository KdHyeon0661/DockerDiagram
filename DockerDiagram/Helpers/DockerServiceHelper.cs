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

        // 도커 프로세스 실행 여부 확인
        public static bool IsDockerRunning()
        {
            var processes = Process.GetProcessesByName(DOCKER_PROCESS_NAME);
            return processes.Length > 0;
        }

        public static async Task StartDockerAsync(ISystemService systemService, IDialogService dialogService)
        {
            if (IsDockerRunning()) return; // 이미 실행 중

            // 실행 파일 경로 찾기
            string? dockerPath = GetDockerExecutablePath();

            // 경로 못 찾으면 사용자에게 묻기
            if (string.IsNullOrEmpty(dockerPath) || !File.Exists(dockerPath))
            {
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
                        return; // 취소
                    }
                }
                else
                {
                    return; // 취소
                }
            }

            // 실행 시도
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dockerPath,
                    UseShellExecute = true
                });

                await WaitForDockerReadyAsync(systemService);
            }
            catch (Exception ex)
            {
                dialogService.ShowMessage($"도커 실행 실패: {ex.Message}");
            }
        }

        private static async Task WaitForDockerReadyAsync(ISystemService systemService)
        {
            int timeoutSeconds = 60;

            for (int i = 0; i < timeoutSeconds; i++)
            {
                if (await systemService.PingAsync())
                {
                    return;
                }

                await Task.Delay(1000);
            }

            throw new TimeoutException("Docker Desktop 실행 시간이 초과되었습니다. (60초)");
        }

        private static string? GetDockerExecutablePath()
        {
            string? path = null;
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

            path = @"C:\Program Files\Docker\Docker\Docker Desktop.exe";
            if (File.Exists(path)) return path;

            return null;
        }
    }
}