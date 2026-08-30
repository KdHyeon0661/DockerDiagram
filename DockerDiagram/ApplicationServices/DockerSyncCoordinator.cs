using DockerDiagram.Contracts;
using Docker.DotNet.Models;
using DockerDiagram.ViewModels;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DockerDiagram.ApplicationServices
{
    /// <summary>
    /// Docker 이벤트 스트림과 보조 주기 동기화의 수명 주기를 관리합니다.
    /// </summary>
    public sealed class DockerSyncCoordinator : IDisposable
    {
        private readonly Func<IDockerService> _getActiveService;
        private readonly ResourceExplorerViewModel _explorer;
        private readonly SheetManagerViewModel _sheetManager;
        private readonly IDialogService _dialogService;
        private readonly DispatcherTimer _autoSyncTimer;
        private readonly DispatcherTimer _eventSyncTimer;
        private bool _isSyncing;
        private CancellationTokenSource? _eventsCts;
        private IDockerService? _eventsService;
        private Task? _eventsTask;
        private bool _disposed;

        public DockerSyncCoordinator(
            Func<IDockerService> getActiveService,
            ResourceExplorerViewModel explorer,
            SheetManagerViewModel sheetManager,
            IDialogService dialogService)
        {
            _getActiveService = getActiveService;
            _explorer = explorer;
            _sheetManager = sheetManager;
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

            _autoSyncTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _autoSyncTimer.Tick += AutoSyncTimer_Tick;
            _autoSyncTimer.Start();

            _eventSyncTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600)
            };
            _eventSyncTimer.Tick += EventSyncTimer_Tick;

            _sheetManager.PropertyChanged += SheetManager_PropertyChanged;
            _ = _explorer.SyncWithDockerEngineAsync();
            RestartEventMonitor();
        }

        public async Task OnDockerStartedAsync()
        {
            Debug.WriteLine("[DockerSync] Docker started signal received. Refreshing...");
            await _explorer.SyncWithDockerEngineAsync();
            await _sheetManager.RestoreLiveStateAsync();
            RestartEventMonitor();
        }

        private async void AutoSyncTimer_Tick(object? sender, EventArgs e)
        {
            await SyncWithGuardAsync();
            RestartEventMonitor();
        }

        private async void EventSyncTimer_Tick(object? sender, EventArgs e)
        {
            _eventSyncTimer.Stop();
            await SyncWithGuardAsync();
        }

        private async Task SyncWithGuardAsync()
        {
            if (_disposed || _isSyncing) return;

            try
            {
                _isSyncing = true;
                await _explorer.SyncWithDockerEngineAsync();
            }
            finally
            {
                _isSyncing = false;
            }
        }

        private void SheetManager_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SheetManagerViewModel.ActiveSheet))
            {
                RestartEventMonitor();
            }
        }

        private void RestartEventMonitor()
        {
            if (_disposed) return;

            var service = _getActiveService();
            if (ReferenceEquals(service, _eventsService) &&
                _eventsCts is { IsCancellationRequested: false } &&
                _eventsTask is { IsCompleted: false })
            {
                return;
            }

            StopEventMonitor();

            _eventsService = service;
            _eventsCts = new CancellationTokenSource();
            var token = _eventsCts.Token;
            _eventsTask = Task.Run(() => MonitorEventsLoopAsync(service, token), token);
        }

        private async Task MonitorEventsLoopAsync(IDockerService service, CancellationToken cancellationToken)
        {
            var progress = new Progress<Message>(message => OnDockerEventReceived(message, cancellationToken));

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await service.MonitorDockerEventsAsync(progress, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (
                    cancellationToken.IsCancellationRequested &&
                    IsExpectedStreamShutdownException(ex))
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DockerEvents] Stream disconnected: {ex.Message}");
                    try
                    {
                        await _dialogService.InvokeOnUiThreadAsync(() =>
                        {
                            if (!_disposed) _explorer.LastSyncTime = "Docker events reconnecting...";
                        });
                    }
                    catch (Exception) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private static bool IsExpectedStreamShutdownException(Exception exception)
        {
            return exception is OperationCanceledException or
                   IOException or
                   ObjectDisposedException;
        }

        private void OnDockerEventReceived(Message message, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || _disposed) return;
            if (!IsDiagramRelevantDockerEvent(message)) return;

            _dialogService.BeginInvokeOnUiThread(() =>
            {
                if (_disposed || cancellationToken.IsCancellationRequested) return;

                var action = string.IsNullOrWhiteSpace(message.Action) ? "changed" : message.Action;
                var type = string.IsNullOrWhiteSpace(message.Type) ? "docker" : message.Type;
                _explorer.LastSyncTime = $"Docker event: {type}/{action}";

                _eventSyncTimer.Stop();
                _eventSyncTimer.Start();
            });
        }

        private static bool IsDiagramRelevantDockerEvent(Message message)
        {
            var type = message.Type ?? string.Empty;
            var action = message.Action ?? string.Empty;
            if (action.StartsWith("exec_", StringComparison.OrdinalIgnoreCase))
                return false;

            return type.Equals("container", StringComparison.OrdinalIgnoreCase) ||
                   type.Equals("volume", StringComparison.OrdinalIgnoreCase) ||
                   type.Equals("network", StringComparison.OrdinalIgnoreCase) ||
                   type.Equals("image", StringComparison.OrdinalIgnoreCase);
        }

        private void StopEventMonitor()
        {
            _eventSyncTimer.Stop();

            CancellationTokenSource? eventsCts = _eventsCts;
            Task? eventsTask = _eventsTask;
            _eventsCts = null;
            _eventsTask = null;
            _eventsService = null;

            if (eventsCts == null) return;

            eventsCts.Cancel();
            if (eventsTask == null)
            {
                eventsCts.Dispose();
                return;
            }

            // 취소 직후 CTS를 폐기하거나 Task 참조를 버리지 않습니다.
            // Events 스트림이 Named Pipe/HTTP 읽기에서 빠져나온 뒤 정리하고,
            // 혹시 남은 fault도 관찰해 UnobservedTaskException을 방지합니다.
            _ = eventsTask.ContinueWith(
                completedTask =>
                {
                    if (completedTask.IsFaulted)
                    {
                        Debug.WriteLine(
                            $"[DockerEvents] Monitor stopped with error: " +
                            $"{completedTask.Exception?.GetBaseException().Message}");
                    }

                    eventsCts.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _sheetManager.PropertyChanged -= SheetManager_PropertyChanged;
            _autoSyncTimer.Stop();
            _autoSyncTimer.Tick -= AutoSyncTimer_Tick;
            _eventSyncTimer.Stop();
            _eventSyncTimer.Tick -= EventSyncTimer_Tick;
            StopEventMonitor();
        }
    }
}
