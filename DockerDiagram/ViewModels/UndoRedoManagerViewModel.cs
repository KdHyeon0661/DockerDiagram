using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using DockerDiagram.Common;
using DockerDiagram.Contracts;

namespace DockerDiagram.ViewModels
{
    public interface IHistoryCommand
    {
        string Description { get; }
        bool AffectsDocker { get; }
        string? MergeKey { get; }
        Task UndoAsync();
        Task RedoAsync();
    }

    public class DelegateHistoryCommand : IHistoryCommand
    {
        private readonly Func<Task> _undo;
        private readonly Func<Task> _redo;

        public string Description { get; }
        public bool AffectsDocker { get; }
        public string? MergeKey { get; }

        public DelegateHistoryCommand(string description, bool affectsDocker, Func<Task> undo, Func<Task> redo, string? mergeKey = null)
        {
            Description = description;
            AffectsDocker = affectsDocker;
            _undo = undo;
            _redo = redo;
            MergeKey = mergeKey;
        }

        public Task UndoAsync() => _undo();
        public Task RedoAsync() => _redo();
    }

    public class UndoRedoManagerViewModel : ViewModelBase
    {
        private readonly IDialogService _dialogService;
        private readonly List<IHistoryCommand> _undoStack = [];
        private readonly List<IHistoryCommand> _redoStack = [];
        private bool _includeDockerResourceHistory;
        private bool _includeVolumeBackupForUndo;
        private bool _isReplaying;
        private string _statusText = "실행 취소 준비";

        public ICommand UndoCommand { get; }
        public ICommand RedoCommand { get; }

        public UndoRedoManagerViewModel(IDialogService dialogService)
        {
            _dialogService = dialogService;
            UndoCommand = new AsyncRelayCommand(UndoAsync, () => CanUndo);
            RedoCommand = new AsyncRelayCommand(RedoAsync, () => CanRedo);
        }

        public bool IncludeDockerResourceHistory
        {
            get => _includeDockerResourceHistory;
            set
            {
                if (value && !_includeDockerResourceHistory)
                {
                    bool confirm = _dialogService.ShowConfirm(
                        "Docker 생성·삭제 기록을 실행 취소하거나 다시 실행하면 실제 컨테이너, 볼륨, 네트워크가 생성되거나 삭제될 수 있습니다.\n\n계속하시겠습니까?",
                        "Docker 실행 취소/다시 실행");

                    if (!confirm)
                    {
                        OnPropertyChanged();
                        return;
                    }
                }

                if (SetProperty(ref _includeDockerResourceHistory, value))
                    RefreshStatus();
            }
        }

        public bool IncludeVolumeBackupForUndo
        {
            get => _includeVolumeBackupForUndo;
            set
            {
                if (value && !_includeVolumeBackupForUndo)
                {
                    bool confirm = _dialogService.ShowConfirm(
                        "Docker 볼륨을 삭제하기 전에 실행 취소용 임시 tar 백업을 만듭니다.\n\n큰 볼륨은 시간과 디스크 공간이 필요할 수 있습니다. 백업 파일은 앱 종료 시 삭제하며, 남은 파일은 다음 시작 때 정리합니다.\n\n계속하시겠습니까?",
                        "볼륨 실행 취소 백업");

                    if (!confirm)
                    {
                        OnPropertyChanged();
                        return;
                    }
                }

                if (SetProperty(ref _includeVolumeBackupForUndo, value))
                    RefreshStatus();
            }
        }

        public bool IsReplaying
        {
            get => _isReplaying;
            private set => SetProperty(ref _isReplaying, value);
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        public bool CanUndo => _undoStack.Count > 0 && !IsReplaying;
        public bool CanRedo => _redoStack.Count > 0 && !IsReplaying;
        public string DiagramStatusText => $"시트 기록만 | 실행 취소 {_undoStack.Count} / 다시 실행 {_redoStack.Count}";

        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
            RefreshStatus();
        }

        public void RecordExecuted(IHistoryCommand command)
        {
            if (IsReplaying) return;
            if (command.AffectsDocker && !IncludeDockerResourceHistory)
            {
                StatusText = "Docker 기록에서 제외됨";
                RaiseCommandStates();
                return;
            }

            if (!string.IsNullOrEmpty(command.MergeKey) &&
                _undoStack.LastOrDefault()?.MergeKey == command.MergeKey)
            {
                _undoStack[^1] = command;
            }
            else
            {
                _undoStack.Add(command);
            }

            _redoStack.Clear();
            TrimStack(_undoStack);
            RefreshStatus();
        }

        public async Task ExecuteAndRecordAsync(IHistoryCommand command)
        {
            await command.RedoAsync();
            RecordExecuted(command);
        }

        private async Task UndoAsync()
        {
            if (!CanUndo) return;
            var command = _undoStack[^1];
            if (!ConfirmDockerHistory(command, "실행 취소")) return;

            try
            {
                IsReplaying = true;
                StatusText = command.AffectsDocker ? $"Docker 실행 취소: {command.Description}" : $"실행 취소: {command.Description}";
                await command.UndoAsync();
                _undoStack.RemoveAt(_undoStack.Count - 1);
                _redoStack.Add(command);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"실행 취소 실패: {ex.Message}", "실행 취소");
                StatusText = "실행 취소 실패";
            }
            finally
            {
                IsReplaying = false;
                RefreshStatus();
            }
        }

        private async Task RedoAsync()
        {
            if (!CanRedo) return;
            var command = _redoStack[^1];
            if (!ConfirmDockerHistory(command, "다시 실행")) return;

            try
            {
                IsReplaying = true;
                StatusText = command.AffectsDocker ? $"Docker 다시 실행: {command.Description}" : $"다시 실행: {command.Description}";
                await command.RedoAsync();
                _redoStack.RemoveAt(_redoStack.Count - 1);
                _undoStack.Add(command);
                TrimStack(_undoStack);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"다시 실행 실패: {ex.Message}", "다시 실행");
                StatusText = "다시 실행 실패";
            }
            finally
            {
                IsReplaying = false;
                RefreshStatus();
            }
        }

        private bool ConfirmDockerHistory(IHistoryCommand command, string action)
        {
            if (!command.AffectsDocker) return true;
            return _dialogService.ShowConfirm(
                $"{action} 작업은 실제 Docker 리소스를 변경합니다.\n\n{command.Description}\n\n계속하시겠습니까?",
                $"Docker {action}");
        }

        private void RefreshStatus()
        {
            int dockerCount = _undoStack.Count(c => c.AffectsDocker) + _redoStack.Count(c => c.AffectsDocker);
            string dockerMode = IncludeDockerResourceHistory ? $"Docker 기록 켜짐 ({dockerCount})" : "Docker 기록 꺼짐";
            if (IncludeVolumeBackupForUndo) dockerMode += " + 볼륨 백업";
            StatusText = $"{dockerMode} | 실행 취소 {_undoStack.Count} / 다시 실행 {_redoStack.Count}";
            OnPropertyChanged(nameof(DiagramStatusText));
            RaiseCommandStates();
        }

        private void RaiseCommandStates()
        {
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            if (UndoCommand is AsyncRelayCommand undo) undo.RaiseCanExecuteChanged();
            if (RedoCommand is AsyncRelayCommand redo) redo.RaiseCanExecuteChanged();
        }

        private static void TrimStack(List<IHistoryCommand> stack)
        {
            const int maxCount = 100;
            if (stack.Count <= maxCount) return;
            stack.RemoveRange(0, stack.Count - maxCount);
        }
    }
}
