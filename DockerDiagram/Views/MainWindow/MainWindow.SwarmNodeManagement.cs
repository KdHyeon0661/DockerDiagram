using DockerDiagram.ApplicationServices;
using DockerDiagram.Contracts;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace DockerDiagram
{
    public partial class MainWindow
    {
        private async void ManageSwarmNodesButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.ActiveSheet?.RuntimeKind != RuntimeKind.DockerSwarm ||
                ViewModel.ActiveSheet.IsRuntimeUnavailable)
            {
                _dialogService.ShowError("활성 Swarm Manager 연결이 없습니다.", "Manage Swarm Nodes");
                return;
            }

            IReadOnlyList<DockerSwarmNode> nodes = ViewModel.Explorer.SwarmNodes.ToList();
            if (nodes.Count == 0)
            {
                _dialogService.ShowInfo("관리할 Swarm node가 없습니다.", "Manage Swarm Nodes");
                return;
            }

            if (nodes.Count == 1)
            {
                await ManageSwarmNodeAsync(nodes[0]);
                return;
            }

            var menu = new ContextMenu
            {
                PlacementTarget = sender as UIElement,
                Placement = PlacementMode.Bottom
            };
            foreach (DockerSwarmNode node in nodes.OrderBy(node => node.Hostname, StringComparer.OrdinalIgnoreCase))
            {
                var item = new MenuItem
                {
                    Header = $"{node.Hostname}  ·  {node.RoleLabel}  ·  {node.Status}",
                    ToolTip = node.Id,
                    Tag = node
                };
                item.Click += ManageSwarmNodeSelection_Click;
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        private async void ManageSwarmNodeSelection_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Tag: DockerSwarmNode node })
                await ManageSwarmNodeAsync(node);
        }

        private async Task ManageSwarmNodeAsync(DockerSwarmNode node)
        {
            if (ViewModel.ActiveSheet?.RuntimeKind != RuntimeKind.DockerSwarm ||
                ViewModel.ActiveSheet.IsRuntimeUnavailable ||
                ViewModel.ActiveSheet.DockerService is not ISwarmNodeMutationService nodeService)
            {
                _dialogService.ShowError("활성 Swarm Manager 연결이 없습니다.", "Manage Swarm Node");
                return;
            }

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                SwarmNodeEditSnapshot snapshot = await nodeService.InspectSwarmNodeAsync(node.Id);
                Mouse.OverrideCursor = null;

                var dialog = new Views.SwarmNodeManagementDialog(snapshot) { Owner = this };
                if (dialog.ShowDialog() != true) return;

                if (dialog.RequestedAction == Views.SwarmNodeManagementAction.Update && dialog.UpdateOptions != null)
                {
                    int managerCount = ViewModel.Explorer.SwarmNodes.Count(candidate =>
                        candidate.Role.Equals("manager", StringComparison.OrdinalIgnoreCase));
                    IReadOnlyList<string> warnings = SwarmNodeSafetyPolicy.GetUpdateWarnings(
                        snapshot,
                        dialog.UpdateOptions,
                        managerCount);
                    if (warnings.Count > 0 && !_dialogService.ShowConfirm(
                            string.Join("\n", warnings) + "\n\n계속하시겠습니까?",
                            "Confirm Swarm Node Update"))
                    {
                        return;
                    }

                    Mouse.OverrideCursor = Cursors.Wait;
                    SwarmNodeMutationResult result = await nodeService.UpdateSwarmNodeAsync(dialog.UpdateOptions);
                    await ViewModel.RefreshRuntimeResourcesAsync();
                    string resultWarnings = result.Warnings.Count == 0
                        ? string.Empty
                        : $"\n\n경고:\n{string.Join("\n", result.Warnings)}";
                    _dialogService.ShowInfo($"'{snapshot.Hostname}' 노드 설정을 갱신했습니다.{resultWarnings}", "Swarm Node Updated");
                }
                else if (dialog.RequestedAction == Views.SwarmNodeManagementAction.Remove)
                {
                    if (snapshot.IsLocalNode)
                    {
                        _dialogService.ShowError(
                            "현재 연결 중인 자기 자신 노드는 여기서 제거할 수 없습니다. Swarm Setup의 Leave를 사용해 주세요.",
                            "Remove Swarm Node");
                        return;
                    }
                    if (snapshot.Role.Equals("manager", StringComparison.OrdinalIgnoreCase))
                    {
                        _dialogService.ShowError(
                            "Manager 노드는 먼저 Worker로 강등한 뒤 제거해야 합니다.",
                            "Remove Swarm Node");
                        return;
                    }

                    bool force = snapshot.Status.Equals("ready", StringComparison.OrdinalIgnoreCase);
                    string warning = force
                        ? $"Ready Worker '{snapshot.Hostname}'을 강제 제거하시겠습니까?\n실행 중인 task가 중단되거나 orphan 상태가 될 수 있습니다."
                        : $"Worker '{snapshot.Hostname}'을 Swarm에서 제거하시겠습니까?";
                    if (!_dialogService.ShowConfirm(warning, force ? "Force Remove Swarm Node" : "Remove Swarm Node"))
                        return;

                    Mouse.OverrideCursor = Cursors.Wait;
                    await nodeService.RemoveSwarmNodeAsync(snapshot.NodeId, force);
                    await ViewModel.RefreshRuntimeResourcesAsync();
                    _dialogService.ShowInfo($"'{snapshot.Hostname}' 노드를 Swarm에서 제거했습니다.", "Swarm Node Removed");
                }
            }
            catch (SwarmNodeVersionConflictException ex)
            {
                await ViewModel.RefreshRuntimeResourcesAsync();
                _dialogService.ShowError(
                    $"다른 작업에서 node가 먼저 변경되었습니다. 최신 상태를 불러왔으므로 다시 시도해 주세요.\n\n{ex.Message}",
                    "Swarm Node Version Conflict");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(
                    $"Swarm node 작업 실패:\n{ex.GetBaseException().Message}",
                    "Manage Swarm Node");
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }
    }
}
