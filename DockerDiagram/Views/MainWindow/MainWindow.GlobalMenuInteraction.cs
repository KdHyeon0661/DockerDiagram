using System.Windows;
using System.Windows.Input;
using DockerDiagram.ViewModels;

namespace DockerDiagram
{
    public partial class MainWindow
    {
        // --- 옵션 팝업 및 기능 버튼 ---

        /// <summary>
        /// 파일/시트/Compose 전역 메뉴를 엽니다.
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

        private void UndoRedoOptionsButton_Click(object sender, RoutedEventArgs e)
        {
            CloseAllOptionPopups();
            UndoRedoOptionsPopup.IsOpen = true;
        }

        private void CanvasSizeButton_Click(object sender, RoutedEventArgs e)
        {
            CloseAllOptionPopups();

            if (DataContext is MainViewModel vm && vm.ActiveSheet != null)
            {
                txtMapWidth.Text = (vm.ActiveSheet.MapWidth / SheetViewModel.MapInputScale).ToString();
                txtMapHeight.Text = (vm.ActiveSheet.MapHeight / SheetViewModel.MapInputScale).ToString();
            }

            MapSizePanel.Visibility = Visibility.Visible;
            CanvasSizePopup.IsOpen = true;
        }

        private void CloseAllOptionPopups()
        {
            OptionPopup.IsOpen = false;
            ImageDockerPopup.IsOpen = false;
            UndoRedoOptionsPopup.IsOpen = false;
            CanvasSizePopup.IsOpen = false;
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

            var window = new Views.ImageImportWindow
            {
                Owner = this
            };

            if (window.ShowDialog() != true) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                await vm.ActiveSheet.DockerService.ImportImageFromTarAsync(window.TarPath, window.Repository, window.ImageTag, window.Message);
                await vm.Explorer.SyncWithDockerEngineAsync();
                _dialogService.ShowInfo($"이미지를 import했습니다.\n{window.Repository}:{window.ImageTag}", "Import Image");
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
                await vm.ActiveSheet.DockerService.LoadImageFromTarAsync(dialog.FileName);
                await vm.Explorer.SyncWithDockerEngineAsync();
                _dialogService.ShowInfo($"이미지를 load했습니다.\n{dialog.FileName}", "Load Image");
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
            var dlg = new Views.BuildImageDialog();
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

        /// <summary>
        /// 맵 크기 조절 패널 닫기 버튼.
        /// </summary>
        private void CloseMapSize_Click(object sender, RoutedEventArgs e)
        {
            MapSizePanel.Visibility = Visibility.Collapsed;
            CanvasSizePopup.IsOpen = false;
        }

        /// <summary>
        /// 사용자가 입력한 가로/세로 값을 검증한 뒤 현재 활성화된 시트(ActiveSheet)의 실제 맵 크기로 즉시 적용(Apply)합니다.
        /// </summary>
        private void ApplyMapSize_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as MainViewModel;
            if (vm?.ActiveSheet == null) return;

            bool hasValidWidth = double.TryParse(txtMapWidth.Text.Trim(), out double width) && double.IsFinite(width);
            bool hasValidHeight = double.TryParse(txtMapHeight.Text.Trim(), out double height) && double.IsFinite(height);

            if (!hasValidWidth || !hasValidHeight)
            {
                _dialogService.ShowMessage("너비와 높이는 숫자로 입력해 주세요.");
                return;
            }

            if (width < SheetViewModel.MinimumMapInputWidth ||
                height < SheetViewModel.MinimumMapInputHeight)
            {
                _dialogService.ShowMessage(
                    $"맵 크기는 너비 {SheetViewModel.MinimumMapInputWidth:0} 이상, 높이 {SheetViewModel.MinimumMapInputHeight:0} 이상이어야 합니다.");
                return;
            }

            double mapWidth = width * SheetViewModel.MapInputScale;
            double mapHeight = height * SheetViewModel.MapInputScale;
            if (!double.IsFinite(mapWidth) || !double.IsFinite(mapHeight))
            {
                _dialogService.ShowMessage("입력한 맵 크기가 너무 큽니다.");
                return;
            }

            vm.ActiveSheet.MapWidth = mapWidth;
            vm.ActiveSheet.MapHeight = mapHeight;
            ZoomPanGrid.UpdateLayout();
            CaptureActiveSheetViewportCenter(markModified: false);
            vm.SheetManager.MarkAsModified();

            MapSizePanel.Visibility = Visibility.Collapsed;
            CloseAllOptionPopups();
        }

    }
}