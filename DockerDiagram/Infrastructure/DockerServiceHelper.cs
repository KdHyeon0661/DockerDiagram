using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using Microsoft.Win32;

namespace DockerDiagram.Infrastructure
{
    public enum DockerStartupPhase
    {
        Checking,
        Launching,
        ProcessRunning,
        WaitingForEngine,
        Ready
    }

    public sealed record DockerStartupProgress(
        DockerStartupPhase Phase,
        TimeSpan Elapsed,
        string Message);

    /// <summary>
    /// 윈도우(Windows) 환경에서 도커 데스크톱(Docker Desktop) 프로세스의 실행 상태를 확인하고,
    /// 필요시 직접 실행하거나 준비 상태를 대기하는 유틸리티 클래스입니다.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class DockerServiceHelper
    {
        private const string DOCKER_PROCESS_NAME = "Docker Desktop"; // 작업 관리자에 표시되는 프로세스 이름
        private const string DOCKER_EXE_NAME = "Docker Desktop.exe"; // 도커 실제 실행 파일명
        private const string DOCKER_APP_PATH_KEY = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + DOCKER_EXE_NAME;
        private const string DOCKER_UNINSTALL_KEY = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Docker Desktop";
        /// <summary>
        /// 현재 PC에서 도커 데스크톱 프로세스가 이미 실행 중인지 확인합니다.
        /// </summary>
        public static bool IsDockerRunning()
        {
            var processes = Process.GetProcessesByName(DOCKER_PROCESS_NAME);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
        }

        /// <summary>
        /// Windows 설치 정보에 등록된 Docker Desktop을 실행하고,
        /// 도커 엔진과 통신이 가능해질 때까지 대기합니다.
        /// </summary>
        public static async Task StartDockerAsync(
            ISystemService systemService,
            IProgress<DockerStartupProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(systemService);
            cancellationToken.ThrowIfCancellationRequested();

            var stopwatch = Stopwatch.StartNew();
            progress?.Report(new DockerStartupProgress(
                DockerStartupPhase.Checking,
                stopwatch.Elapsed,
                "Docker 엔진 연결을 확인하는 중입니다."));

            if (await systemService.PingAsync().WaitAsync(cancellationToken))
            {
                progress?.Report(new DockerStartupProgress(
                    DockerStartupPhase.Ready,
                    stopwatch.Elapsed,
                    "Docker 엔진이 준비되었습니다."));
                return;
            }

            Process? launchedProcess = null;
            if (!IsDockerRunning())
            {
                progress?.Report(new DockerStartupProgress(
                    DockerStartupPhase.Launching,
                    stopwatch.Elapsed,
                    "Docker Desktop 실행 파일을 찾는 중입니다."));

                string dockerPath = GetRegisteredDockerExecutablePath()
                    ?? throw new FileNotFoundException(
                        "Windows 설치 정보에 등록된 Docker Desktop 실행 파일을 찾을 수 없습니다.");

                launchedProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = dockerPath,
                    WorkingDirectory = Path.GetDirectoryName(dockerPath) ?? string.Empty,
                    UseShellExecute = true
                });

                if (launchedProcess == null)
                    throw new InvalidOperationException("Windows에서 Docker Desktop 실행 요청을 처리하지 못했습니다.");

                progress?.Report(new DockerStartupProgress(
                    DockerStartupPhase.Launching,
                    stopwatch.Elapsed,
                    "Docker Desktop 실행을 요청했습니다."));
            }
            else
            {
                progress?.Report(new DockerStartupProgress(
                    DockerStartupPhase.ProcessRunning,
                    stopwatch.Elapsed,
                    "Docker Desktop 프로세스가 실행 중입니다."));
            }

            try
            {
                await WaitForDockerReadyAsync(
                    systemService,
                    launchedProcess,
                    stopwatch,
                    progress,
                    cancellationToken);
            }
            finally
            {
                launchedProcess?.Dispose();
            }
        }

        /// <summary>
        /// 고정 시간 제한 없이 도커 엔진이 정상적으로 응답(Ping)할 때까지 상태를 확인합니다.
        /// 사용자가 대기를 취소하거나 실제 Docker Desktop 프로세스가 종료된 경우에만 중단합니다.
        /// </summary>
        private static async Task WaitForDockerReadyAsync(
            ISystemService systemService,
            Process? launchedProcess,
            Stopwatch stopwatch,
            IProgress<DockerStartupProgress>? progress,
            CancellationToken cancellationToken)
        {
            bool processObserved = IsDockerRunning();
            int consecutiveMissingProcessChecks = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (await systemService.PingAsync().WaitAsync(cancellationToken))
                {
                    progress?.Report(new DockerStartupProgress(
                        DockerStartupPhase.Ready,
                        stopwatch.Elapsed,
                        "Docker 엔진 연결이 완료되었습니다."));
                    return;
                }

                bool processRunning = IsDockerRunning();
                if (processRunning)
                {
                    processObserved = true;
                    consecutiveMissingProcessChecks = 0;
                }
                else if (processObserved || launchedProcess?.HasExited == true)
                {
                    consecutiveMissingProcessChecks++;
                    if (consecutiveMissingProcessChecks >= 3)
                    {
                        throw new InvalidOperationException(
                            "Docker Desktop 프로세스가 엔진 준비 전에 종료되었습니다.");
                    }
                }

                progress?.Report(new DockerStartupProgress(
                    processRunning ? DockerStartupPhase.WaitingForEngine : DockerStartupPhase.Launching,
                    stopwatch.Elapsed,
                    processRunning
                        ? "Docker Desktop은 실행 중이며 엔진이 준비되기를 기다리고 있습니다."
                        : "Docker Desktop 프로세스가 시작되기를 기다리고 있습니다."));

                await Task.Delay(DockerConnectionTiming.ServiceStartupPollInterval, cancellationToken);
            }
        }

        /// <summary>
        /// Docker Desktop 설치 프로그램이 등록한 사용자/시스템 설치 정보에서
        /// 실제 실행 파일의 절대 경로를 가져옵니다.
        /// </summary>
        private static string? GetRegisteredDockerExecutablePath()
        {
            (string KeyPath, string? ValueName, bool IsInstallDirectory)[] registrations =
            {
                ($@"HKEY_CURRENT_USER\{DOCKER_APP_PATH_KEY}", null, false),
                ($@"HKEY_LOCAL_MACHINE\{DOCKER_APP_PATH_KEY}", null, false),
                ($@"HKEY_CURRENT_USER\{DOCKER_UNINSTALL_KEY}", "InstallLocation", true),
                ($@"HKEY_LOCAL_MACHINE\{DOCKER_UNINSTALL_KEY}", "InstallLocation", true)
            };

            foreach ((string keyPath, string? valueName, bool isInstallDirectory) in registrations)
            {
                try
                {
                    string? registeredPath = Registry.GetValue(keyPath, valueName, null) as string;
                    if (string.IsNullOrWhiteSpace(registeredPath)) continue;

                    string resolvedPath = Environment
                        .ExpandEnvironmentVariables(registeredPath)
                        .Trim()
                        .Trim('"');
                    string candidate = isInstallDirectory
                        ? Path.Combine(resolvedPath, DOCKER_EXE_NAME)
                        : resolvedPath;

                    if (File.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }
                catch (Exception ex)
                {
                    AppLog.Warning($"Docker 설치 정보 조회 실패: {keyPath}", ex);
                }
            }

            return null;
        }

    }
}
