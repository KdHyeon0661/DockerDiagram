using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;

namespace DockerDiagram.Helpers
{
    [SupportedOSPlatform("windows")]
    public static class DockerServiceHelper
    {
        // 1. 유지보수를 위해 상수를 상단으로 분리했습니다.
        private const string DOCKER_PROCESS_NAME = "Docker Desktop";
        private const string DOCKER_EXE_NAME = "Docker Desktop.exe";
        private const string DOCKER_REGISTRY_KEY = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Docker Desktop";
        private const int DOCKER_TIMEOUT_SECONDS = 60; // 타임아웃 시간 (초)

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
                        FileName = DOCKER_EXE_NAME
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
                // 타임아웃 발생 시에도 여기서 잡혀서 메시지가 표시됩니다.
                dialogService.ShowMessage($"도커 실행 실패: {ex.Message}");
            }
        }

        private static async Task WaitForDockerReadyAsync(ISystemService systemService)
        {
            // 상수를 사용하여 타임아웃 제어
            for (int i = 0; i < DOCKER_TIMEOUT_SECONDS; i++)
            {
                if (await systemService.PingAsync())
                {
                    return; // 연결 성공
                }

                await Task.Delay(1000);
            }

            throw new TimeoutException($"Docker Desktop 실행 시간이 초과되었습니다. ({DOCKER_TIMEOUT_SECONDS}초)");
        }

        private static string? GetDockerExecutablePath()
        {
            string? path = null;

            // 2. 레지스트리 조회 시 64비트 뷰를 명시적으로 사용합니다.
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = baseKey.OpenSubKey(DOCKER_REGISTRY_KEY))
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
                Debug.WriteLine($"[DockerDiscovery] 레지스트리 접근 실패: {ex.Message}");
            }

            // 환경변수 경로 찾기
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

            // 최후의 수단: 기본 설치 경로 하드코딩 확인
            path = @"C:\Program Files\Docker\Docker\Docker Desktop.exe";
            if (File.Exists(path)) return path;

            return null;
        }
    }
}