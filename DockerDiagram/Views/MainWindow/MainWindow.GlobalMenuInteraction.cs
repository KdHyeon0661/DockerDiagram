using System.Linq;
using System.Windows;
using System.Windows.Input;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Diagram;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram
{
    public partial class MainWindow
    {
        // --- 옵션 팝업 및 기능 버튼 ---

        /// <summary>
        /// 파일/시트 전역 메뉴를 엽니다.
        /// </summary>
        private void OptionButton_Click(object sender, RoutedEventArgs e)
        {
            CloseAllOptionPopups();
            OptionPopup.IsOpen = true;
        }

        private void ImageDockerButton_Click(object sender, RoutedEventArgs e)
        {
            CloseAllOptionPopups();
            ImageDockerPopup.IsOpen = true;
        }

        private void SheetNavigationButton_Click(object sender, RoutedEventArgs e)
        {
            CloseAllOptionPopups();
            SheetNavigationPopup.IsOpen = true;
        }

        private void SheetSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            CloseAllOptionPopups();

            if (DataContext is not MainViewModel vm || vm.ActiveSheet == null)
            {
                _dialogService.ShowInfo("활성화된 시트가 없습니다.", "시트 설정");
                return;
            }

            var window = new Views.SheetSettingsWindow(vm, _dialogService)
            {
                Owner = this
            };
            if (window.ShowDialog() == true && window.SheetSizeChanged)
            {
                ZoomPanGrid.UpdateLayout();
                CaptureActiveSheetViewportCenter(markModified: false);
                vm.SheetManager.MarkAsModified();
            }
        }

        private void CloseAllOptionPopups()
        {
            OptionPopup.IsOpen = false;
            ImageDockerPopup.IsOpen = false;
            SheetNavigationPopup.IsOpen = false;
        }

        private void FitSheetItems_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.ActiveSheet is not SheetViewModel sheet)
                return;

            Rect? bounds = CalculateSheetItemBounds(sheet.Nodes.Cast<ConnectableItemViewModel>()
                .Concat(sheet.Groups));
            if (bounds == null)
            {
                _dialogService.ShowInfo("현재 시트에 배치된 항목이 없습니다.", "시트 탐색");
                return;
            }

            FitSheetBounds(sheet, bounds.Value);
        }

        private void FocusSheetSelection_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.ActiveSheet is not SheetViewModel sheet)
                return;

            Rect? bounds = CalculateSheetItemBounds(ViewModel.Inspector.SelectedCanvasItems);
            if (bounds == null)
            {
                _dialogService.ShowInfo("먼저 시트에서 이동할 항목을 선택해 주세요.", "시트 탐색");
                return;
            }

            if (!TryGetViewportSize(out double viewportWidth, out double viewportHeight))
                return;

            double scale = double.IsFinite(sheet.Scale) && sheet.Scale > 0 ? sheet.Scale : 1.0;
            ApplySheetView(
                sheet,
                scale,
                bounds.Value.Left + (bounds.Value.Width / 2.0),
                bounds.Value.Top + (bounds.Value.Height / 2.0),
                viewportWidth,
                viewportHeight);
        }

        private void ResetSheetZoom_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.ActiveSheet is not SheetViewModel sheet ||
                !TryGetViewportSize(out double viewportWidth, out double viewportHeight))
            {
                return;
            }

            double currentScale = double.IsFinite(sheet.Scale) && sheet.Scale > 0 ? sheet.Scale : 1.0;
            double centerX = ((viewportWidth / 2.0) - sheet.OffsetX) / currentScale;
            double centerY = ((viewportHeight / 2.0) - sheet.OffsetY) / currentScale;
            ApplySheetView(sheet, 1.0, centerX, centerY, viewportWidth, viewportHeight);
        }

        private void CenterSheet_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.ActiveSheet is not SheetViewModel sheet ||
                !TryGetViewportSize(out double viewportWidth, out double viewportHeight))
            {
                return;
            }

            double scale = double.IsFinite(sheet.Scale) && sheet.Scale > 0 ? sheet.Scale : 1.0;
            ApplySheetView(
                sheet,
                scale,
                sheet.MapWidth / 2.0,
                sheet.MapHeight / 2.0,
                viewportWidth,
                viewportHeight);
        }

        private void FitSheetBounds(SheetViewModel sheet, Rect bounds)
        {
            if (!TryGetViewportSize(out double viewportWidth, out double viewportHeight))
                return;

            const double padding = 72.0;
            const double minimumScale = 0.05;
            double requiredWidth = Math.Max(1.0, bounds.Width + (padding * 2));
            double requiredHeight = Math.Max(1.0, bounds.Height + (padding * 2));
            double scale = Math.Clamp(
                Math.Min(viewportWidth / requiredWidth, viewportHeight / requiredHeight),
                minimumScale,
                SheetViewModel.MaximumScale);
            ApplySheetView(
                sheet,
                scale,
                bounds.Left + (bounds.Width / 2.0),
                bounds.Top + (bounds.Height / 2.0),
                viewportWidth,
                viewportHeight);
        }

        private void ApplySheetView(
            SheetViewModel sheet,
            double scale,
            double centerX,
            double centerY,
            double viewportWidth,
            double viewportHeight)
        {
            sheet.Scale = Math.Clamp(scale, 0.05, SheetViewModel.MaximumScale);
            sheet.OffsetX = (viewportWidth / 2.0) - (centerX * sheet.Scale);
            sheet.OffsetY = (viewportHeight / 2.0) - (centerY * sheet.Scale);
            sheet.IsViewportInitialized = true;
            sheet.CaptureViewportCenter(viewportWidth, viewportHeight);
            ViewModel.SheetManager.MarkAsModified();
            SheetNavigationPopup.IsOpen = false;
        }

        private bool TryGetViewportSize(out double width, out double height)
        {
            width = ViewportCanvas.ActualWidth;
            height = ViewportCanvas.ActualHeight;
            return double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0;
        }

        private static Rect? CalculateSheetItemBounds(IEnumerable<ConnectableItemViewModel> items)
        {
            Rect bounds = Rect.Empty;
            foreach (ConnectableItemViewModel item in items)
            {
                if (!double.IsFinite(item.X) || !double.IsFinite(item.Y) ||
                    !double.IsFinite(item.Width) || !double.IsFinite(item.Height))
                {
                    continue;
                }

                Rect itemBounds = new(item.X, item.Y, Math.Max(1.0, item.Width), Math.Max(1.0, item.Height));
                if (bounds.IsEmpty)
                    bounds = itemBounds;
                else
                    bounds.Union(itemBounds);
            }

            return bounds.IsEmpty ? null : bounds;
        }

        private async void RefreshSwarmCluster_Click(object sender, RoutedEventArgs e)
        {
            CloseAllOptionPopups();

            if (DataContext is not MainViewModel vm ||
                vm.ActiveSheet?.RuntimeKind != RuntimeKind.DockerSwarm ||
                vm.ActiveSheet.DockerService is not ISwarmService)
            {
                _dialogService.ShowInfo("활성 Swarm Manager 세션이 없습니다.", "Docker Swarm");
                return;
            }

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                await vm.RefreshRuntimeResourcesAsync();
                var nodes = vm.Explorer.SwarmNodes.ToList();

                int managers = nodes.Count(node =>
                    node.Role.Equals("manager", System.StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrWhiteSpace(node.ManagerStatus));
                int ready = nodes.Count(node => node.Status.Equals("ready", System.StringComparison.OrdinalIgnoreCase));
                _dialogService.ShowInfo(
                    $"클러스터 정보를 갱신했습니다.\n노드 {nodes.Count}개 · Manager {managers}개 · Ready {ready}개",
                    "Docker Swarm");
            }
            catch (System.Exception ex)
            {
                _dialogService.ShowError($"Swarm 클러스터 갱신 실패:\n{ex.GetBaseException().Message}", "Docker Swarm");
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void OpenSwarmSetup_Click(object sender, RoutedEventArgs e)
        {
            CloseAllOptionPopups();

            if (DataContext is not MainViewModel vm ||
                vm.ActiveSheet?.DockerService is not ISwarmService swarmService)
            {
                _dialogService.ShowInfo("Swarm 상태를 확인할 Docker 연결이 없습니다.", "Docker Swarm");
                return;
            }

            using var dialog = new Views.SwarmSetupDialog(swarmService, _dialogService, vm.DockerServiceFactory,
                suggestLocalAddresses: vm.ActiveSheet.Profile?.Type == EndpointType.Local)
            {
                Owner = this
            };
            dialog.ShowDialog();
        }

        /// <summary>
        /// 톱니바퀴(옵션) 팝업 메뉴에서 [이미지 관리] 버튼을 클릭했을 때 호출됩니다.
        /// 사용하지 않는 이미지를 정리하고 검색할 수 있는 전용 팝업 창을 엽니다.
        /// </summary>
        private void ManageImages_Click(object sender, RoutedEventArgs e)
        {
            // 1. 톱니바퀴 드롭다운 팝업 닫기
            CloseAllOptionPopups();

            // 2. 새 창 띄우기 (데이터 공유)
            var imgWindow = new Views.ImageManagerWindow
            {
                DataContext = ViewModel.Explorer,
                Owner = this
            };
            imgWindow.ShowDialog();
        }

        private async void ImportImageFromTar_Click(object sender, RoutedEventArgs e)
        {
            CloseAllOptionPopups();

            if (DataContext is not MainViewModel vm || vm.ActiveSheet == null)
            {
                _dialogService.ShowInfo("활성화된 시트가 없습니다.", "Import Image");
                return;
            }

            var window = new Views.ImageImportWindow(_dialogService)
            {
                Owner = this
            };

            if (window.ShowDialog() != true) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                await vm.ActiveSheet.DockerService.ImportImageFromTarAsync(
                    window.TarPath,
                    window.Repository,
                    window.ImageTag,
                    window.Message,
                    vm.LifetimeToken);
                await vm.RefreshRuntimeResourcesAsync();
                _dialogService.ShowInfo($"이미지를 import했습니다.\n{window.Repository}:{window.ImageTag}", "Import Image");
            }
            catch (OperationCanceledException) when (vm.LifetimeToken.IsCancellationRequested)
            {
                AppLog.Info($"이미지 import가 애플리케이션 종료로 취소되었습니다: {window.TarPath}");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"이미지 import 실패: {ex.Message}", "Import Image");
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private async void LoadImageFromTar_Click(object sender, RoutedEventArgs e)
        {
            CloseAllOptionPopups();

            if (DataContext is not MainViewModel vm || vm.ActiveSheet == null)
            {
                _dialogService.ShowInfo("활성화된 시트가 없습니다.", "Load Image");
                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Load Docker Image Tar",
                Filter = "Tar file (*.tar)|*.tar|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) != true) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                await vm.ActiveSheet.DockerService.LoadImageFromTarAsync(
                    dialog.FileName,
                    vm.LifetimeToken);
                await vm.RefreshRuntimeResourcesAsync();
                _dialogService.ShowInfo($"이미지를 load했습니다.\n{dialog.FileName}", "Load Image");
            }
            catch (OperationCanceledException) when (vm.LifetimeToken.IsCancellationRequested)
            {
                AppLog.Info($"이미지 load가 애플리케이션 종료로 취소되었습니다: {dialog.FileName}");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"이미지 load 실패: {ex.Message}", "Load Image");
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private async void MenuBuildImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Views.BuildImageDialog(_dialogService);
            dlg.Owner = this;

            if (dlg.ShowDialog() == true)
            {
                if (this.DataContext is ViewModels.MainViewModel vm)
                {
                    await vm.BuildImageOnlyAsync(dlg.TagName, dlg.DockerfileContent, dlg.DockerfilePath);
                }
            }
        }

        private void ShowDiskUsage_Click(object sender, RoutedEventArgs e)
        {
            CloseAllOptionPopups();

            if (ViewModel.ActiveSheet == null)
            {
                _dialogService.ShowInfo("활성화된 시트가 없습니다.", "Docker Disk Usage");
                return;
            }

            var profileName = ViewModel.ActiveSheet.Profile?.Name;
            if (string.IsNullOrWhiteSpace(profileName))
            {
                profileName = "Docker";
            }

            var window = new Views.SystemDiskUsageWindow(
                ViewModel.ActiveSheet.DockerService,
                _dialogService,
                profileName)
            {
                Owner = this
            };

            window.ShowDialog();
        }

    }
}
