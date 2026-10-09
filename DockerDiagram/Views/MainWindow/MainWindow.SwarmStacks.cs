using DockerDiagram.ApplicationServices;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace DockerDiagram
{
    public partial class MainWindow
    {
        private SwarmStackWindowController? _swarmStackController;
        private SwarmStackWindowController SwarmStackController =>
            _swarmStackController ??= new SwarmStackWindowController(this);

        private void InitializeSwarmStackController() => _ = SwarmStackController;

        private void SheetTab_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is not Border { DataContext: SheetViewModel sheet } border ||
                !SwarmStackSheetRegistry.IsStackSheet(sheet) ||
                border.ContextMenu == null)
            {
                return;
            }

            SwarmStackController.EnsureStackMenu(border.ContextMenu, sheet);
        }

        private sealed class SwarmStackWindowController : IDisposable
        {
            private const string MenuMarker = "DockerDiagram.SwarmStack.Menu";
            private readonly MainWindow _window;
            private readonly DockerCliSwarmStackService _deploymentService = new();
            private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(90);
            private readonly SwarmStackSheetSynchronizer _synchronizer = new();
            private readonly CancellationTokenSource _lifetimeCancellation = new();
            private bool _refreshing;
            private bool _disposed;

            public SwarmStackWindowController(MainWindow window)
            {
                _window = window;
                _window.ViewModel.Explorer.PropertyChanged += Explorer_PropertyChanged;
                _window.ViewModel.SheetManager.PropertyChanged += SheetManager_PropertyChanged;
                _window.Closed += Window_Closed;
                _ = RefreshActiveStackAsync();
            }

            public async Task CreateStackSheetAsync()
            {
                ConnectionWorkspaceViewModel? workspace = _window.ViewModel.SheetManager.ActiveWorkspace;
                if (workspace?.RuntimeKind != RuntimeKind.DockerSwarm) return;

                int ordinal = workspace.Sheets.Count + 1;
                IReadOnlyList<string> reservedNames = GetReservedStackNames(workspace);
                var draftState = new SwarmStackSheetState
                {
                    StackName = SwarmStackIdentityPolicy.SuggestUnique($"stack-{ordinal}", reservedNames, ordinal),
                    DeployState = SwarmStackDeployState.Draft,
                    RuntimeSummary = "Draft stack"
                };
                var dialog = new Views.SwarmStackDialog(draftState, reservedNames) { Owner = _window };
                if (dialog.ShowDialog() != true) return;

                _window.ViewModel.SheetManager.AddSwarmStackSheet();
                SheetViewModel? sheet = _window.ViewModel.ActiveSheet;
                if (sheet == null) return;
                SwarmStackSheetState state = SwarmStackSheetRegistry.GetOrCreate(sheet, ordinal);
                ApplyOptions(sheet, state, dialog.Options);
                state.DeployState = SwarmStackDeployState.Draft;
                SwarmStackSheetRegistry.Persist(sheet, state);
                _window.ViewModel.IsModified = true;

                if (dialog.DeployRequested)
                    await DeployAsync(sheet, state, dialog.Options);
            }

            public void EnsureStackMenu(ContextMenu contextMenu, SheetViewModel sheet)
            {
                MenuItem? existingStatus = contextMenu.Items
                    .OfType<MenuItem>()
                    .FirstOrDefault(item => Equals(item.Tag, MenuMarker) && !item.IsEnabled);
                if (existingStatus != null)
                {
                    SwarmStackSheetState existingState = SwarmStackSheetRegistry.GetOrCreate(sheet);
                    existingStatus.Header = $"Stack: {existingState.StackName} · {existingState.DeployState} · {existingState.RuntimeSummary}";
                    return;
                }

                var separator = new Separator { Tag = MenuMarker };
                var status = new MenuItem { Tag = MenuMarker, IsEnabled = false };
                var configure = new MenuItem { Tag = MenuMarker, Header = "Stack 설정·배포..." };
                var refresh = new MenuItem { Tag = MenuMarker, Header = "Stack 상태 새로고침" };
                var remove = new MenuItem { Tag = MenuMarker, Header = "Docker에서 Stack 제거", Foreground = System.Windows.Media.Brushes.Firebrick };

                SwarmStackSheetState state = SwarmStackSheetRegistry.GetOrCreate(sheet);
                status.Header = $"Stack: {state.StackName} · {state.DeployState}";
                configure.Click += async (_, _) => await ConfigureAsync(sheet);
                refresh.Click += async (_, _) => await RefreshStackAsync(sheet, showResult: true);
                remove.Click += async (_, _) => await RemoveAsync(sheet);

                contextMenu.Items.Insert(0, separator);
                contextMenu.Items.Insert(0, remove);
                contextMenu.Items.Insert(0, refresh);
                contextMenu.Items.Insert(0, configure);
                contextMenu.Items.Insert(0, status);
            }

            private async Task ConfigureAsync(SheetViewModel sheet)
            {
                SwarmStackSheetState state = SwarmStackSheetRegistry.GetOrCreate(sheet);
                ConnectionWorkspaceViewModel? workspace = _window.ViewModel.SheetManager.ActiveWorkspace;
                IReadOnlyList<string> reservedNames = workspace == null
                    ? Array.Empty<string>()
                    : GetReservedStackNames(workspace, sheet);
                var dialog = new Views.SwarmStackDialog(state, reservedNames) { Owner = _window };
                if (dialog.ShowDialog() != true) return;

                ApplyOptions(sheet, state, dialog.Options);
                if (!dialog.DeployRequested)
                {
                    state.DeployState = SwarmStackDeployState.Draft;
                    state.LastError = string.Empty;
                    state.RuntimeSummary = "Draft saved";
                    SwarmStackSheetRegistry.Persist(sheet, state);
                    _window.ViewModel.IsModified = true;
                    FilterExplorerForStack(sheet, state);
                    return;
                }

                await DeployAsync(sheet, state, dialog.Options);
            }

            private async Task DeployAsync(
                SheetViewModel sheet,
                SwarmStackSheetState state,
                SwarmStackDeploymentOptions options)
            {
                string preview = SwarmStackCommandBuilder.Preview(
                    SwarmStackCommandBuilder.BuildDeployArguments(options, "<temporary-stack.yml>"));
                if (!_window._dialogService.ShowConfirm(
                        $"다음 Stack을 현재 Swarm Manager에 배포하시겠습니까?\n\n{preview}",
                        "Deploy Swarm Stack"))
                {
                    return;
                }

                state.DeployState = SwarmStackDeployState.Deploying;
                state.LastError = string.Empty;
                state.RuntimeSummary = "Deploying...";
                SwarmStackSheetRegistry.Persist(sheet, state);
                _window.ViewModel.IsModified = true;
                _window._dialogService.SetBusyCursor(true);
                try
                {
                    using CancellationTokenSource operation = CreateOperationCancellation();
                    SwarmStackCommandResult result = await _deploymentService.DeployAsync(
                        options,
                        sheet.Profile,
                        operation.Token);
                    if (!result.Success)
                    {
                        state.DeployState = SwarmStackDeployState.Error;
                        state.LastError = string.IsNullOrWhiteSpace(result.CombinedOutput)
                            ? $"docker stack deploy exited with code {result.ExitCode}."
                            : result.CombinedOutput;
                        state.RuntimeSummary = "Deployment failed";
                        SwarmStackSheetRegistry.Persist(sheet, state);
                        _window._dialogService.ShowError(state.LastError, "Deploy Swarm Stack");
                        return;
                    }

                    state.LastDeployedAt = DateTimeOffset.Now;
                    state.DeployState = SwarmStackDeployState.Deploying;
                    await _window.ViewModel.RefreshRuntimeResourcesAsync().WaitAsync(operation.Token);
                    SwarmStackRuntimeSnapshot snapshot = await _synchronizer.WaitForDeploymentAsync(
                        sheet,
                        state,
                        operation.Token);
                    state.DeployState = SwarmStackDeployState.Deployed;
                    state.RuntimeSummary = snapshot.Services.Count == 0
                        ? "Deploy accepted · services are still converging"
                        : snapshot.Summary;
                    state.LastError = string.Empty;
                    SwarmStackSheetRegistry.Persist(sheet, state);
                    FilterExplorerForStack(sheet, state);
                    _window._dialogService.ShowInfo(
                        $"'{state.StackName}' Stack 배포가 승인되었습니다.\n{state.RuntimeSummary}",
                        "Deploy Swarm Stack");
                }
                catch (OperationCanceledException) when (!_lifetimeCancellation.IsCancellationRequested)
                {
                    state.DeployState = SwarmStackDeployState.Error;
                    state.LastError = $"Stack 배포가 {OperationTimeout.TotalSeconds:0}초 안에 완료되지 않아 중단되었습니다.";
                    state.RuntimeSummary = "Deployment timed out";
                    SwarmStackSheetRegistry.Persist(sheet, state);
                    _window._dialogService.ShowError(state.LastError, "Deploy Swarm Stack");
                }
                catch (OperationCanceledException)
                {
                    state.DeployState = SwarmStackDeployState.Error;
                    state.LastError = "Application shutdown canceled the Stack deployment.";
                    state.RuntimeSummary = "Deployment canceled";
                    SwarmStackSheetRegistry.Persist(sheet, state);
                }
                catch (Exception ex)
                {
                    state.DeployState = SwarmStackDeployState.Error;
                    state.LastError = ex.GetBaseException().Message;
                    state.RuntimeSummary = "Deployment failed";
                    SwarmStackSheetRegistry.Persist(sheet, state);
                    _window._dialogService.ShowError(state.LastError, "Deploy Swarm Stack");
                }
                finally
                {
                    _window._dialogService.SetBusyCursor(false);
                    _window.ViewModel.IsModified = true;
                }
            }

            private async Task RemoveAsync(SheetViewModel sheet)
            {
                SwarmStackSheetState state = SwarmStackSheetRegistry.GetOrCreate(sheet);
                if (!_window._dialogService.ShowConfirm(
                        $"Swarm Stack '{state.StackName}'을 Docker에서 제거하시겠습니까?\n" +
                        "시트와 YAML은 오프라인 기록으로 유지됩니다.",
                        "Remove Swarm Stack"))
                {
                    return;
                }

                _window._dialogService.SetBusyCursor(true);
                try
                {
                    using CancellationTokenSource operation = CreateOperationCancellation();
                    SwarmStackCommandResult result = await _deploymentService.RemoveAsync(
                        state.StackName,
                        sheet.Profile,
                        operation.Token);
                    if (!result.Success)
                    {
                        state.DeployState = SwarmStackDeployState.Error;
                        state.LastError = string.IsNullOrWhiteSpace(result.CombinedOutput)
                            ? $"docker stack rm exited with code {result.ExitCode}."
                            : result.CombinedOutput;
                        SwarmStackSheetRegistry.Persist(sheet, state);
                        _window._dialogService.ShowError(state.LastError, "Remove Swarm Stack");
                        return;
                    }

                    SwarmStackSheetSynchronizer.MarkRemoved(sheet, state);
                    state.LastError = string.Empty;
                    SwarmStackSheetRegistry.Persist(sheet, state);
                    _window.ViewModel.IsModified = true;
                    _window._dialogService.ShowInfo(
                        $"'{state.StackName}' Stack 제거가 승인되었습니다. 서비스 제거 상태는 주기적으로 갱신됩니다.",
                        "Remove Swarm Stack");
                }
                catch (OperationCanceledException) when (!_lifetimeCancellation.IsCancellationRequested)
                {
                    state.DeployState = SwarmStackDeployState.Error;
                    state.LastError = $"Stack 제거가 {OperationTimeout.TotalSeconds:0}초 안에 완료되지 않아 중단되었습니다.";
                    state.RuntimeSummary = "Removal timed out";
                    SwarmStackSheetRegistry.Persist(sheet, state);
                    _window._dialogService.ShowError(state.LastError, "Remove Swarm Stack");
                }
                catch (OperationCanceledException)
                {
                    state.DeployState = SwarmStackDeployState.Error;
                    state.LastError = "Application shutdown canceled the Stack removal.";
                    state.RuntimeSummary = "Removal canceled";
                    SwarmStackSheetRegistry.Persist(sheet, state);
                }
                catch (Exception ex)
                {
                    state.DeployState = SwarmStackDeployState.Error;
                    state.LastError = ex.GetBaseException().Message;
                    SwarmStackSheetRegistry.Persist(sheet, state);
                    _window._dialogService.ShowError(state.LastError, "Remove Swarm Stack");
                }
                finally
                {
                    _window._dialogService.SetBusyCursor(false);
                }
            }

            private async Task RefreshStackAsync(SheetViewModel sheet, bool showResult)
            {
                SwarmStackSheetState state = SwarmStackSheetRegistry.GetOrCreate(sheet);
                try
                {
                    using CancellationTokenSource operation = CreateOperationCancellation();
                    SwarmStackRuntimeSnapshot snapshot = await _synchronizer.SynchronizeAsync(
                        sheet,
                        state,
                        operation.Token);
                    SwarmStackSheetRegistry.Persist(sheet, state);
                    FilterExplorerForStack(sheet, state);
                    if (showResult)
                        _window._dialogService.ShowInfo(snapshot.Summary, $"Stack: {state.StackName}");
                }
                catch (OperationCanceledException) when (!_lifetimeCancellation.IsCancellationRequested)
                {
                    if (showResult)
                        _window._dialogService.ShowError(
                            $"Stack 상태 조회가 {OperationTimeout.TotalSeconds:0}초 안에 완료되지 않았습니다.",
                            "Refresh Swarm Stack");
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    if (showResult)
                        _window._dialogService.ShowError(ex.GetBaseException().Message, "Refresh Swarm Stack");
                }
            }

            private async Task RefreshActiveStackAsync()
            {
                if (_disposed || _refreshing) return;
                SheetViewModel? sheet = _window.ViewModel.ActiveSheet;
                if (!SwarmStackSheetRegistry.IsStackSheet(sheet) || sheet!.IsRuntimeUnavailable) return;

                _refreshing = true;
                try { await RefreshStackAsync(sheet, showResult: false); }
                finally { _refreshing = false; }
            }

            private void Explorer_PropertyChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName != nameof(ResourceExplorerViewModel.LastSyncTime)) return;
                string status = _window.ViewModel.Explorer.LastSyncTime;
                if (!status.StartsWith("Last updated:", StringComparison.OrdinalIgnoreCase) &&
                    !status.StartsWith("Partial sync:", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                _ = RefreshActiveStackAsync();
            }

            private void SheetManager_PropertyChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(SheetManagerViewModel.ActiveSheet))
                    _ = RefreshActiveStackAsync();
            }

            private void FilterExplorerForStack(SheetViewModel sheet, SwarmStackSheetState state)
            {
                if (!ReferenceEquals(sheet, _window.ViewModel.ActiveSheet)) return;
                for (int index = _window.ViewModel.Explorer.ExistingContainers.Count - 1; index >= 0; index--)
                {
                    DockerContainer service = _window.ViewModel.Explorer.ExistingContainers[index];
                    if (!service.IsSwarmService ||
                        !string.Equals(service.ComposeProjectName, state.StackName, StringComparison.OrdinalIgnoreCase))
                    {
                        _window.ViewModel.Explorer.ExistingContainers.RemoveAt(index);
                    }
                }
            }

            private static IReadOnlyList<string> GetReservedStackNames(
                ConnectionWorkspaceViewModel workspace,
                SheetViewModel? excludedSheet = null) =>
                workspace.Sheets
                    .Where(sheet => !ReferenceEquals(sheet, excludedSheet))
                    .Where(SwarmStackSheetRegistry.IsStackSheet)
                    .Select(sheet => SwarmStackSheetRegistry.GetOrCreate(sheet).StackName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToArray();

            private CancellationTokenSource CreateOperationCancellation()
            {
                var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
                operation.CancelAfter(OperationTimeout);
                return operation;
            }

            private static void ApplyOptions(
                SheetViewModel sheet,
                SwarmStackSheetState state,
                SwarmStackDeploymentOptions options)
            {
                state.StackName = options.StackName;
                state.SourcePath = options.SourcePath;
                state.Yaml = options.Yaml;
                sheet.Title = options.StackName;
            }

            private void Window_Closed(object? sender, EventArgs e) => Dispose();

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _lifetimeCancellation.Cancel();
                _window.ViewModel.Explorer.PropertyChanged -= Explorer_PropertyChanged;
                _window.ViewModel.SheetManager.PropertyChanged -= SheetManager_PropertyChanged;
                _window.Closed -= Window_Closed;
                _lifetimeCancellation.Dispose();
            }
        }
    }
}
