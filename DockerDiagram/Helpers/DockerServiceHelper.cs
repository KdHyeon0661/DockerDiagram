using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows; // MessageBox용
using System.Runtime.Versioning;

namespace DockerDiagram.Helpers
{
    [SupportedOSPlatform("windows")]
    public static class DockerServiceHelper
    {
        private const string DOCKER_PROCESS_NAME = "Docker Desktop";
        private const string DOCKER_EXE_NAME = "Docker Desktop.exe";

        public static bool IsDockerRunning()
        {
            var processes = Process.GetProcessesByName(DOCKER_PROCESS_NAME);
            return processes.Length > 0;
        }

        public static async Task StartDockerAsync()
        {
            if (IsDockerRunning()) return;

            // 1. 동적으로 실행 파일 경로 찾기
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

            // 3. 실행
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dockerPath,
                    UseShellExecute = true
                });

                // 실행 대기
                await Task.Delay(15000);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"도커 실행 실패: {ex.Message}");
            }
        }

        // 설치 경로를 동적으로 찾는 로직
        private static string? GetDockerExecutablePath()
        {
            string? path = null;

            // [방법 1] 레지스트리 Uninstall 정보에서 찾기
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

            // [방법 2] 환경변수 %ProgramFiles% 이용
            try
            {
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                path = Path.Combine(programFiles, "Docker", "Docker", DOCKER_EXE_NAME);
                if (File.Exists(path)) return path;
            }
            catch { }

            // [방법 3] 기본 C드라이브 경로
            path = @"C:\Program Files\Docker\Docker\Docker Desktop.exe";
            if (File.Exists(path)) return path;

            return null; // 못 찾음
        }
    }
}