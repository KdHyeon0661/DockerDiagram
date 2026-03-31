using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace DockerDiagram.Helpers
{
    /// <summary>
    /// 윈도우(Windows) 환경에서 도커 데스크톱(Docker Desktop) 프로세스의 실행 상태를 확인하고,
    /// 필요시 직접 실행하거나 준비 상태를 대기하는 유틸리티 클래스입니다.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class DockerServiceHelper
    {
        private const string DOCKER_PROCESS_NAME = "Docker Desktop"; // 작업 관리자에 표시되는 프로세스 이름
        private const string DOCKER_EXE_NAME = "Docker Desktop.exe"; // 도커 실제 실행 파일명
        private const string DOCKER_REGISTRY_KEY = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Docker Desktop"; // 설치 경로를 찾기 위한 레지스트리 경로
        private const int DOCKER_TIMEOUT_SECONDS = 60; // 도커 실행 후 응답을 기다릴 최대 시간 (초)

        /// <summary>
        /// 현재 PC에서 도커 데스크톱 프로세스가 이미 실행 중인지 확인합니다.
        /// </summary>
        public static bool IsDockerRunning()
        {
            var processes = Process.GetProcessesByName(DOCKER_PROCESS_NAME);
            return processes.Length > 0;
        }

        /// <summary>
        /// 도커 데스크톱을 백그라운드에서 실행하고, 도커 엔진과 통신이 가능해질 때까지 대기합니다.
        /// 실행 파일을 자동으로 찾지 못하면 사용자에게 직접 파일을 선택하도록 다이얼로그를 띄웁니다.
        /// </summary>
        public static async Task StartDockerAsync(ISystemService systemService, IDialogService dialogService)
        {
            if (IsDockerRunning()) return; // 이미 실행 중이면 무시

            string? dockerPath = GetDockerExecutablePath(); // 실행 파일 경로 찾기

            // 경로를 못 찾으면 사용자에게 직접 선택할지 묻기
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
                        return; // 파일 선택 취소 시 프로세스 중단
                    }
                }
                else
                {
                    return; // 경로 지정 거부 시 프로세스 중단
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

                await WaitForDockerReadyAsync(systemService); // 도커 엔진이 켜질 때까지 대기
            }
            catch (Exception ex)
            {
                // 권한 부족이나 타임아웃 발생 시 에러 메시지 팝업
                dialogService.ShowMessage($"도커 실행 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 도커 엔진이 정상적으로 응답(Ping)할 때까지 최대 지정된 시간(기본 60초) 동안 1초 간격으로 확인합니다.
        /// </summary>
        private static async Task WaitForDockerReadyAsync(ISystemService systemService)
        {
            for (int i = 0; i < DOCKER_TIMEOUT_SECONDS; i++)
            {
                if (await systemService.PingAsync())
                {
                    return; // 연결 성공!
                }

                await Task.Delay(1000); // 1초 대기 후 재시도
            }

            throw new TimeoutException($"Docker Desktop 실행 시간이 초과되었습니다. ({DOCKER_TIMEOUT_SECONDS}초)");
        }

        /// <summary>
        /// 레지스트리, 환경변수, 하드코딩된 기본 경로를 순차적으로 탐색하여 도커 데스크톱 실행 파일(.exe)의 위치를 찾아 반환합니다.
        /// </summary>
        private static string? GetDockerExecutablePath()
        {
            string? path = null;

            // 1. 레지스트리 조회 (64비트 뷰 명시적 사용)
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

            // 2. 환경변수 경로 찾기 (Program Files 등 교차 검증)
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

            // 3. 최후의 수단: 기본 설치 경로 하드코딩 확인
            path = @"C:\Program Files\Docker\Docker\Docker Desktop.exe";
            if (File.Exists(path)) return path;

            return null; // 모든 방법 실패 시 null 반환
        }
    }
}