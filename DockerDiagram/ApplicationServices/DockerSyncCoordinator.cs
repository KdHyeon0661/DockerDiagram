using DockerDiagram.Contracts;
using Docker.DotNet.Models;
using DockerDiagram.ViewModels;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Threading;

namespace DockerDiagram.ApplicationServices
{
    /// <summary>
    /// Docker 이벤트 스트림과 보조 주기 동기화의 수명 주기를 관리합니다.
    /// 실행 중 들어온 갱신 요청은 한 번으로 합쳐 후속 실행하므로 이벤트 유실이 없습니다.
    /// </summary>
    public sealed class DockerSyncCoordinator : IDisposable
    {
        private readonly Func<IDockerService> _getActiveService;
        private readonly ResourceExplorerViewModel _explorer;
        private readonly SheetManagerViewModel _sheetManager;
        private readonly IDialogService _dialogService;
        private readonly DispatcherTimer _autoSyncTimer;
        private readonly DispatcherTimer _eventSyncTimer;
        private readonly CoalescingAsyncRequestPump _syncPump;
        private readonly SwarmRuntimeRecoveryService _swarmRecovery;
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
            _swarmRecovery = new SwarmRuntimeRecoveryService(explorer);
            _syncPump = new CoalescingAsyncRequestPump(SyncOnceAsync);

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
            _ = _syncPump.RequestAsync();
            RestartEventMonitor();
        }

        public async Task OnDockerStartedAsync()
        {
            Debug.WriteLine("[DockerSync] Docker started signal received. Refreshing...");
            await _syncPump.RequestAsync();
            await _sheetManager.RestoreLiveStateAsync();
            await _syncPump.RequestAsync();
            RestartEventMonitor();
        }

        public Task RequestSyncAsync() => _syncPump.RequestAsync();

        private async Task SyncOnceAsync()
        {
            if (_disposed) return;

            SheetViewModel? sheet = _sheetManager.ActiveSheet;
            IDockerService service = _getActiveService();
            SwarmRuntimeRecoveryService.SwarmSyncCheckpoint? checkpoint = _swarmRecovery.Capture(sheet);

            try
            {
                await _explorer.SyncCoreWithDockerEngineAsync(sheet, service);
                await _swarmRecovery.ReconcileAsync(
                    checkpoint,
                    _sheetManager.ActiveSheet);
            }
            catch (Exception ex)
            {
                // 타이머/이벤트 기반 fire-and-forget 호출에서 예외가 UI dispatcher까지 전파되지 않게 합니다.
                Debug.WriteLine($"[DockerSync] Refresh failed: {ex.GetBaseException().Message}");
                if (ReferenceEquals(sheet, _sheetManager.ActiveSheet))
                    _explorer.LastSyncTime = "Sync failed; retry scheduled";
            }
        }

        private async void AutoSyncTimer_Tick(object? sender, EventArgs e)
        {
            await _syncPump.RequestAsync();
            RestartEventMonitor();
        }

        private async void EventSyncTimer_Tick(object? sender, EventArgs e)
        {
            _eventSyncTimer.Stop();
            await _syncPump.RequestAsync();
        }

        private void SheetManager_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SheetManagerViewModel.ActiveSheet)) return;

            _eventSyncTimer.Stop();
            RestartEventMonitor();
            _ = _syncPump.RequestAsync();
        }

        private void RestartEventMonitor()
        {
            if (_disposed) return;

            IDockerService service = _getActiveService();
            if (ReferenceEquals(service, _eventsService) &&
                _eventsCts is { IsCancellationRequested: false } &&
                _eventsTask is { IsCompleted: false })
            {
                return;
            }

            StopEventMonitor();

            _eventsService = service;
            _eventsCts = new CancellationTokenSource();
            CancellationToken token = _eventsCts.Token;
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
                    if (!cancellationToken.IsCancellationRequested)
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
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
                            if (!_disposed) _explorer.LastSyncTime = "Docker events reconnecting; snapshot preserved";
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

        private static bool IsExpectedStreamShutdownException(Exception exception) =>
            exception is OperationCanceledException or IOException or ObjectDisposedException;

        private void OnDockerEventReceived(Message message, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || _disposed) return;
            if (!IsDiagramRelevantDockerEvent(message)) return;

            _dialogService.BeginInvokeOnUiThread(() =>
            {
                if (_disposed || cancellationToken.IsCancellationRequested) return;

                string action = string.IsNullOrWhiteSpace(message.Action) ? "changed" : message.Action;
                string type = string.IsNullOrWhiteSpace(message.Type) ? "docker" : message.Type;
                _explorer.LastSyncTime = $"Docker event: {type}/{action}";

                // 마지막 관련 이벤트 이후 600ms가 지나면 한 번만 동기화합니다.
                _eventSyncTimer.Stop();
                _eventSyncTimer.Start();
            });
        }

        public static bool IsDiagramRelevantDockerEvent(Message message)
        {
            string type = message.Type ?? string.Empty;
            string action = message.Action ?? string.Empty;
            if (action.StartsWith("exec_", StringComparison.OrdinalIgnoreCase))
                return false;

            return type.Equals("container", StringComparison.OrdinalIgnoreCase) ||
                   type.Equals("volume", StringComparison.OrdinalIgnoreCase) ||
                   type.Equals("network", StringComparison.OrdinalIgnoreCase) ||
                   type.Equals("image", StringComparison.OrdinalIgnoreCase) ||
                   type.Equals("service", StringComparison.OrdinalIgnoreCase) ||
                   type.Equals("task", StringComparison.OrdinalIgnoreCase) ||
                   type.Equals("node", StringComparison.OrdinalIgnoreCase) ||
                   type.Equals("secret", StringComparison.OrdinalIgnoreCase) ||
                   type.Equals("config", StringComparison.OrdinalIgnoreCase) ||
                   type.Equals("swarm", StringComparison.OrdinalIgnoreCase);
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
            _syncPump.Dispose();
            StopEventMonitor();
        }
    }
}
