using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using DockerDiagram.Helpers;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    /// <summary>
    /// 상단 툴바(메뉴)에 배치되는 전역 기능들(화면 비우기, 도커 시스템 청소, Compose 연동 등)을 전담하는 Sub-ViewModel입니다.
    /// </summary>
    public class ToolboxViewModel : ViewModelBase
    {
        private readonly MainViewModel _mainVm;
        private readonly IDockerService _defaultDockerService;
        private readonly IDialogService _dialogService;

        public ICommand FlowClearCommand { get; }
        public ICommand FlowAllClearCommand { get; }
        public ICommand SystemPruneCommand { get; }
        public ICommand ImportComposeCommand { get; }
        public ICommand ExportComposeCommand { get; }

        public ToolboxViewModel(MainViewModel mainVm, IDockerService defaultDockerService, IDialogService dialogService)
        {
            _mainVm = mainVm;
            _defaultDockerService = defaultDockerService;
            _dialogService = dialogService;

            FlowClearCommand = new RelayCommand(ExecuteFlowClear);
            FlowAllClearCommand = new RelayCommand(ExecuteFlowAllClear);
            SystemPruneCommand = new AsyncRelayCommand(ExecuteSystemPruneAsync);

            ExportComposeCommand = new RelayCommand(_ =>
            {
                if (_mainVm.ActiveSheet != null)
                {
                    ComposeExportService.ExportToCompose(_mainVm.ActiveSheet, _dialogService);
                }
            });

            ImportComposeCommand = new AsyncRelayCommand(async _ =>
            {
                // 현재 탭의 접속 상태에 맞는 도커 서비스를 유동적으로 주입
                var activeService = _mainVm.ActiveSheet?.DockerService ?? _defaultDockerService;

                await ComposeImportService.ImportFromCompose(
                    _mainVm,
                    (IContainerService)activeService,
                    (IVolumeService)activeService,
                    (INetworkService)activeService,
                    _dialogService
                );
            });
        }

        /// <summary>
        /// 현재 활성화된 시트(도화지)의 모든 요소(노드, 선, 그룹)를 깨끗하게 지웁니다.
        /// </summary>
        private void ExecuteFlowClear(object? obj)
        {
            if (_mainVm.ActiveSheet != null && _dialogService.ShowConfirm("현재 시트의 모든 내용을 지우시겠습니까?", "Flow Clear"))
            {
                _mainVm.ActiveSheet.Nodes.Clear();
                _mainVm.ActiveSheet.Connectors.Clear();
                _mainVm.ActiveSheet.Groups.Clear();
                _mainVm.IsModified = true;
            }
        }

        /// <summary>
        /// 열려있는 모든 시트의 내용을 일괄적으로 초기화합니다.
        /// </summary>
        private void ExecuteFlowAllClear(object? obj)
        {
            if (_dialogService.ShowConfirm("모든 시트의 내용을 초기화 하시겠습니까?", "Flow All Clear"))
            {
                foreach (var sheet in _mainVm.Sheets)
                {
                    sheet.Nodes.Clear();
                    sheet.Connectors.Clear();
                    sheet.Groups.Clear();
                }
                _mainVm.IsModified = true;
            }
        }

        /// <summary>
        /// 도커 리소스 대청소(Prune) 명령을 실행하고 결과를 사용자에게 안내합니다.
        /// </summary>
        private async Task ExecuteSystemPruneAsync(object? obj)
        {
            var dlg = new Views.PruneDialog();
            dlg.Owner = System.Windows.Application.Current.MainWindow;

            if (dlg.ShowDialog() != true) return;

            string targetCommand = dlg.FinalCommand;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                string pruneResult = "";
                await Task.Run(() =>
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {targetCommand}",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using (var process = Process.Start(startInfo))
                    {
                        if (process != null)
                        {
                            pruneResult = process.StandardOutput.ReadToEnd();
                            process.WaitForExit();
                        }
                    }
                });

                // 청소가 끝났으니 사이드바(Explorer) 담당자에게 새로고침을 지시
                if (_mainVm.Explorer != null)
                {
                    await _mainVm.Explorer.SyncWithDockerEngineAsync();
                }

                _dialogService.ShowInfo($"명령어 실행: {targetCommand}\n\n[결과]\n{pruneResult.Trim()}", "청소 완료");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"청소 중 오류 발생: {ex.Message}", "오류");
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }
    }
}