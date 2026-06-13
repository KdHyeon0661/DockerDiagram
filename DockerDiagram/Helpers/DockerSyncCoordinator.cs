using Docker.DotNet.Models;
using DockerDiagram.ViewModels;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace DockerDiagram.Helpers
{
    /// <summary>
    /// Docker 이벤트 스트림과 보조 주기 동기화의 수명 주기를 관리합니다.
    /// </summary>
    public sealed class DockerSyncCoordinator : IDisposable
    {
        private readonly Func<IDockerService> _getActiveService;
        private readonly ResourceExplorerViewModel _explorer;
        private readonly SheetManagerViewModel _sheetManager;
        private readonly DispatcherTimer _autoSyncTimer;
        private readonly DispatcherTimer _eventSyncTimer;
        private CancellationTokenSource? _eventsCts;
        private IDockerService? _eventsService;
        private Task? _eventsTask;
        private bool _isSyncing;
        private bool _disposed;

        public DockerSyncCoordinator(
            Func<IDockerService> getActiveService,
            ResourceExplorerViewModel explorer,
            SheetManagerViewModel sheetManager)
        {
            _getActiveService = getActiveService;
            _explorer = explorer;
            _sheetManager = sheetManager;

            _autoSyncTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
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
            if (_isSyncing) return;

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
                _eventsCts != null &&
                !_eventsCts.IsCancellationRequested)
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
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DockerEvents] Stream disconnected: {ex.Message}");
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (!_disposed) _explorer.LastSyncTime = "Docker events reconnecting...";
                    });

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

        private void OnDockerEventReceived(Message message, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || _disposed) return;
            if (!IsDiagramRelevantDockerEvent(message)) return;

            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed || cancellationToken.IsCancellationRequested) return;

                var action = string.IsNullOrWhiteSpace(message.Action) ? "changed" : message.Action;
                var type = string.IsNullOrWhiteSpace(message.Type) ? "docker" : message.Type;
                _explorer.LastSyncTime = $"Docker event: {type}/{action}";

                _eventSyncTimer.Stop();
                _eventSyncTimer.Start();
            }));
        }

        private static bool IsDiagramRelevantDockerEvent(Message message)
        {
            var type = message.Type ?? string.Empty;
            return type.Equals("container", StringComparison.OrdinalIgnoreCase) ||
                   type.Equals("volume", StringComparison.OrdinalIgnoreCase) ||
                   type.Equals("network", StringComparison.OrdinalIgnoreCase) ||
                   type.Equals("image", StringComparison.OrdinalIgnoreCase);
        }

        private void StopEventMonitor()
        {
            _eventSyncTimer.Stop();

            if (_eventsCts != null)
            {
                _eventsCts.Cancel();
                _eventsCts.Dispose();
                _eventsCts = null;
            }

            _eventsService = null;
            _eventsTask = null;
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
