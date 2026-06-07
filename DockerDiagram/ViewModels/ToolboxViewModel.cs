using System;
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
        private readonly IComposeService _composeService;

        public ICommand FlowClearCommand { get; }
        public ICommand FlowAllClearCommand { get; }
        public ICommand SystemPruneCommand { get; }
        public ICommand ImportComposeCommand { get; }
        public ICommand ExportComposeCommand { get; }

        public ToolboxViewModel(MainViewModel mainVm, IDockerService defaultDockerService, IDialogService dialogService, IComposeService composeService)
        {
            _mainVm = mainVm;
            _defaultDockerService = defaultDockerService;
            _dialogService = dialogService;
            _composeService = composeService;

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
                    _dialogService,
                    _composeService
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
            if (!_dialogService.TryShowPruneOptionsDialog(out var pruneOptions)) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                if (pruneOptions.Target == DockerPruneTarget.Volume)
                {
                    await ExecuteSafeVolumePruneAsync();
                    return;
                }

                var service = (ISystemService)(_mainVm.ActiveSheet?.DockerService ?? _defaultDockerService);
                var pruneResult = await service.PruneAsync(pruneOptions);

                // 청소가 끝났으니 사이드바(Explorer) 담당자에게 새로고침을 지시
                if (_mainVm.Explorer != null)
                {
                    await _mainVm.Explorer.SyncWithDockerEngineAsync();
                }

                _dialogService.ShowInfo($"Docker Engine API prune 실행\n\n[결과]\n{pruneResult.Summary}", "청소 완료");
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

        private async Task ExecuteSafeVolumePruneAsync()
        {
            var service = (IVolumeService)(_mainVm.ActiveSheet?.DockerService ?? _defaultDockerService);
            var volumes = await service.GetVolumesAsync();
            var unusedVolumes = new System.Collections.Generic.List<DockerVolume>();

            foreach (var volume in volumes)
            {
                if (string.IsNullOrWhiteSpace(volume.Name)) continue;
                var users = await service.GetContainersUsingVolumeAsync(volume.Name);
                if (users.Count == 0)
                    unusedVolumes.Add(volume);
            }

            if (unusedVolumes.Count == 0)
            {
                _dialogService.ShowInfo("삭제할 미사용 볼륨이 없습니다.", "Volume Prune");
                return;
            }

            string volumeList = string.Join("\n", unusedVolumes.ConvertAll(v => $"- {v.Name}"));
            if (!_dialogService.ShowConfirm(
                    $"다음 미사용 볼륨 {unusedVolumes.Count}개를 삭제하시겠습니까?\n\n{volumeList}",
                    "Volume Prune"))
            {
                return;
            }

            var deleted = new System.Collections.Generic.List<string>();
            var failed = new System.Collections.Generic.List<string>();

            foreach (var volume in unusedVolumes)
            {
                try
                {
                    await service.RemoveVolumeAsync(volume.Name, force: false);
                    deleted.Add(volume.Name);
                }
                catch (Exception ex)
                {
                    failed.Add($"{volume.Name}: {ex.Message}");
                }
            }

            if (_mainVm.Explorer != null)
                await _mainVm.Explorer.SyncWithDockerEngineAsync();

            string result = $"삭제됨: {deleted.Count}개";
            if (deleted.Count > 0)
                result += "\n\n" + string.Join("\n", deleted.ConvertAll(v => $"- {v}"));
            if (failed.Count > 0)
                result += $"\n\n실패: {failed.Count}개\n" + string.Join("\n", failed.ConvertAll(v => $"- {v}"));

            _dialogService.ShowInfo(result, "Volume Prune 완료");
        }
    }
}
