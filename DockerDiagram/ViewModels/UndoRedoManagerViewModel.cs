using DockerDiagram.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

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
        private readonly List<IHistoryCommand> _undoStack = new();
        private readonly List<IHistoryCommand> _redoStack = new();
        private bool _includeDockerResourceHistory;
        private bool _includeVolumeBackupForUndo;
        private bool _isReplaying;
        private string _statusText = "Undo ready";

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
                        "Docker create/delete history can create or delete real containers, volumes, and networks during Undo/Redo.\n\nContinue?",
                        "Docker Undo/Redo");

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
                        "When deleting a Docker volume, the app will first create a temporary tar backup for Undo.\n\nLarge volumes may take time and disk space. Backup files are deleted when the app exits, and orphaned files are cleaned on next startup.\n\nContinue?",
                        "Volume Undo Backup");

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
                StatusText = "Docker history skipped";
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
            if (!ConfirmDockerHistory(command, "Undo")) return;

            try
            {
                IsReplaying = true;
                StatusText = command.AffectsDocker ? $"Undo Docker: {command.Description}" : $"Undo: {command.Description}";
                await command.UndoAsync();
                _undoStack.RemoveAt(_undoStack.Count - 1);
                _redoStack.Add(command);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Undo failed: {ex.Message}", "Undo");
                StatusText = "Undo failed";
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
            if (!ConfirmDockerHistory(command, "Redo")) return;

            try
            {
                IsReplaying = true;
                StatusText = command.AffectsDocker ? $"Redo Docker: {command.Description}" : $"Redo: {command.Description}";
                await command.RedoAsync();
                _redoStack.RemoveAt(_redoStack.Count - 1);
                _undoStack.Add(command);
                TrimStack(_undoStack);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Redo failed: {ex.Message}", "Redo");
                StatusText = "Redo failed";
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
                $"{action} will change real Docker resources.\n\n{command.Description}\n\nContinue?",
                $"{action} Docker");
        }

        private void RefreshStatus()
        {
            int dockerCount = _undoStack.Count(c => c.AffectsDocker) + _redoStack.Count(c => c.AffectsDocker);
            string dockerMode = IncludeDockerResourceHistory ? $"Docker history on ({dockerCount})" : "Docker history off";
            if (IncludeVolumeBackupForUndo) dockerMode += " + volume backup";
            StatusText = $"{dockerMode} | Undo {_undoStack.Count} / Redo {_redoStack.Count}";
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
