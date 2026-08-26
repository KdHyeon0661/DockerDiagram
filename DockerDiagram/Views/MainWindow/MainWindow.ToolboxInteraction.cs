using DockerDiagram.Diagram;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Infrastructure;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Diagnostics;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram
{
    public partial class MainWindow
    {
        private void Tool_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 1. 드래그 시작 위치 저장 및 초기화
            _toolStartPoint = e.GetPosition(null);
            _isToolDragging = false;

            // 2. 클릭한 도구의 타입 확인 (Tag 속성 가져오기)
            var border = sender as Border;
            string typeStr = border?.Tag?.ToString() ?? "";

            // 1) Network 버튼 클릭
            if (typeStr == "Network")
            {
                _pendingExistingNetwork = null;
                _isNetworkDrawingMode = true;
                _isGroupingMode = false;       // 그룹 모드 끄기
                Mouse.OverrideCursor = Cursors.Cross; // 십자가 커서
                (DataContext as MainViewModel)?.Inspector.ClearSelection();
                e.Handled = true; // 이벤트 소비 (DoDragDrop 방지)
                return;
            }

            // 2) Group 버튼 클릭
            if (typeStr == "Group")
            {
                _isGroupingMode = true;
                _isNetworkDrawingMode = false; // 네트워크 모드 끄기
                Mouse.OverrideCursor = Cursors.Cross; // 십자가 커서
                (DataContext as MainViewModel)?.Inspector.ClearSelection();
                e.Handled = true; // 이벤트 소비 (DoDragDrop 방지)
                return;
            }

            _isNetworkDrawingMode = false;
            _isGroupingMode = false;
            _pendingExistingNetwork = null;
            Mouse.OverrideCursor = null;   // 커서 원래대로 (화살표)
        }

        /// <summary>
        /// 왼쪽 사이드바에서 마우스를 누른 채 이동할 때 호출됩니다.
        /// 일정 거리(5픽셀) 이상 움직이면 '드래그 앤 드롭'의 시작으로 간주하고,
        /// 선택한 도구(컨테이너, 볼륨, 템플릿 등)의 타입에 맞는 DataObject를 생성하여 OS 수준의 드래그 파이프라인(DoDragDrop)에 태워 보냅니다.
        /// </summary>
        // 2. 마우스 이동 (네트워크 모드면 드래그 앤 드롭 방지 + 기존 목록 드래그 처리)
        private void Tool_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isNetworkDrawingMode) return; // 네트워크 그리기 모드 중이면 중단

            if (e.LeftButton == MouseButtonState.Pressed && !_isToolDragging)
            {
                if (Math.Abs(e.GetPosition(null).X - _toolStartPoint.X) > 5)
                {
                    _isToolDragging = true;
                    var border = sender as Border;
                    if (border == null) return;

                    // 드래그할 데이터 객체 준비
                    DockerResource? container = null;

                    // [CASE 1] 템플릿 목록에서 드래그 (DataContext가 TemplateItem인 경우)
                    if (border.DataContext is TemplateItem template)
                    {
                        container = new DockerContainer
                        {
                            Name = template.Name,
                            Image = template.Image,
                            Id = "" // New임을 표시
                        };
                    }
                    // [CASE 2] 상단 고정 버튼에서 드래그 (Tag가 문자열인 경우)
                    else if (border.Tag is string tagStr)
                    {
                        if (tagStr == "Container")
                        {
                            container = new DockerContainer
                            {
                                Name = "New Container",
                                Image = "New Container",
                                Id = ""
                            };
                        }
                        else if (tagStr == "Volume")
                        {
                            // Canvas_Drop에서 DockerContainer로 캐스팅해서 받으므로 타입을 맞춰줍니다.
                            container = new DockerVolume
                            {
                                Name = "New Volume",
                                Id = ""
                            };
                        }
                        else if (tagStr == "Internet")
                        {
                            container = new DockerInternet
                            {
                                Name = "Internet",
                                Id = ""
                            };
                        }
                    }

                    // 데이터가 준비되었으면 드래그 시작
                    if (container != null)
                    {
                        DataObject data = new DataObject("DockerContainerObject", container);
                        DragDrop.DoDragDrop(border, data, DragDropEffects.Copy);
                    }

                    _isToolDragging = false;
                }
            }
        }

        /// <summary>
        /// 왼쪽 사이드바 도구를 드래그하지 않고 단순히 '클릭(Click)'하고 마우스를 뗐을 때 호출됩니다.
        /// 드래그 앤 드롭 없이 현재 보이는 캔버스의 중앙에 요소를 생성하는 다이얼로그 팝업(ContainerDialog, VolumeDialog 등)을 띄워줍니다.
        /// </summary>
        private async void Tool_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isToolDragging)
            {
                var border = sender as Border;
                string typeStr = border?.Tag?.ToString() ?? "";

                if (typeStr == "Network" || typeStr == "Group") return;

                if (Enum.TryParse(typeStr, out NodeType type))
                {
                    var vm = DataContext as MainViewModel;
                    if (vm != null)
                    {
                        Point placement = GetViewportCenteredPlacement(160, 80);
                        double defaultX = placement.X;
                        double defaultY = placement.Y;

                        if (type == NodeType.Container)
                        {
                            var dlg = new Views.ContainerDialog(_dialogService, vm.ActiveSheet?.DockerService);
                            dlg.Owner = this;

                            if (dlg.ShowDialog() == true)
                            {
                                try
                                {
                                    Mouse.OverrideCursor = Cursors.Wait;

                                    // 선택한 생성 방식에 맞는 입력을 처리합니다.
                                    if (dlg.SelectedCreationMode == 0) // 1. [🎛️ 직접 설정 (UI)] 탭
                                    {
                                        // 이미지 이름과 태그(:)를 안전하게 분리
                                        var imageReference = DockerImageReferenceParser.Split(dlg.ImageName);

                                        await vm.CreateNewContainerNodeAsync(
                                            dlg.ContainerName, imageReference.Repository, imageReference.Tag,
                                            dlg.Ports, dlg.EnvVars,
                                            dlg.Volumes, dlg.RestartPolicy, dlg.MemoryMb, dlg.CpuCount, defaultX, defaultY,
                                            dlg.SelectedNetwork, dlg.Command, dlg.IsInteractive,
                                            dlg.RegUser, dlg.RegPass, dlg.RegServer);
                                    }
                                    else if (dlg.SelectedCreationMode == 1) // 2. [💻 명령어로 생성 (CLI)] 탭
                                    {
                                        await vm.ProcessCliCommandAsync(dlg.CliCommand, defaultX, defaultY);
                                    }
                                    else if (dlg.SelectedCreationMode == 2) // 3. [🛠️ 도커파일로 빌드] 탭
                                    {
                                        await vm.BuildImageAndCreateNodeAsync(dlg.BuildImageTag, dlg.DockerfileContent, dlg.UploadedDockerfilePath, defaultX, defaultY);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _dialogService.ShowMessage($"에러 : {ex.Message}");
                                }
                                finally
                                {
                                    Mouse.OverrideCursor = null;
                                }
                            }
                        }
                        else if (type == NodeType.Volume)
                        {
                            var dlg = new Views.VolumeDialog(_dialogService);
                            dlg.Owner = this;
                            if (dlg.ShowDialog() == true)
                            {
                                await vm.CreateNewVolumeNodeAsync(dlg.CreateOptions, defaultX, defaultY);
                            }
                        }
                        else if (type == NodeType.Internet)
                        {
                            await vm.CreateNodeAtAsync(new DockerInternet { Name = "Internet" }, defaultX, defaultY);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 사이드바에서 드래그한 도구나 기존 도커 리소스를 다이어그램 캔버스(도화지)에 떨어뜨렸을 때(Drop) 발생하는 핵심 이벤트입니다.
        /// 드롭된 화면 좌표를 캔버스 내부 좌표(World Point)로 변환하고 10px 단위 그리드에 스냅(Snap)한 뒤,
        /// 데이터 타입과 생성 모드(새로 만들기 vs 기존 요소 배치)에 따라 알맞은 다이얼로그를 띄우거나 노드를 즉시 배치합니다.
        /// </summary>
        // 캔버스 드롭 핸들러 (네트워크 삭제 반영)
        private async void Canvas_Drop(object sender, DragEventArgs e)
        {
            var vm = DataContext as MainViewModel;
            if (vm?.ActiveSheet == null) return;

            // 마우스 드롭 좌표를 캔버스 내부 좌표로 변환 및 10단위 스냅(Snap) 처리
            Point mouseOnScreen = e.GetPosition((UIElement)ZoomPanGrid.Parent);
            Point worldPos = ZoomPanGrid.RenderTransform.Inverse.Transform(mouseOnScreen);
            double snapX = Math.Round((worldPos.X - 80) / 10) * 10;
            double snapY = Math.Round((worldPos.Y - 40) / 10) * 10;

            bool needsLayerUpdate = false; // 레이어 업데이트 필요 여부 체크

            if (e.Data.GetDataPresent("ComposeProjectObject") &&
                e.Data.GetData("ComposeProjectObject") is DockerComposeProject composeProject)
            {
                await vm.PlaceComposeProjectAsync(composeProject, snapX, snapY, centerOnPoint: true);
                return;
            }

            if (e.Data.GetDataPresent("StackTemplateObject") &&
                e.Data.GetData("StackTemplateObject") is StackTemplateDefinition stackTemplate)
            {
                await ShowStackTemplateDialogAndApplyAsync(stackTemplate, snapX, snapY);
                return;
            }

            // [CASE A] 모든 노드 리소스 처리 (DockerResource로 받기)
            if (e.Data.GetDataPresent("DockerContainerObject"))
            {
                var d = e.Data.GetData("DockerContainerObject") as DockerResource;
                if (d == null) return;

                if (string.IsNullOrEmpty(d.Id)) // "New" 아이템 (새로 생성해야 하는 객체)
                {
                    if (d is DockerContainer container)
                    {
                        var dlg = new Views.ContainerDialog(_dialogService, vm.ActiveSheet?.DockerService);
                        dlg.Owner = this;
                        if (container.Image != "New Container") dlg.ImageName = container.Image;

                        if (dlg.ShowDialog() == true)
                        {
                            try
                            {
                                Mouse.OverrideCursor = Cursors.Wait;

                                // 선택한 생성 방식에 맞는 입력을 처리합니다.
                                if (dlg.SelectedCreationMode == 0) // 1. [🎛️ 직접 설정 (UI)] 탭
                                {
                                    var imageReference = DockerImageReferenceParser.Split(dlg.ImageName);

                                    await vm.CreateNewContainerNodeAsync(dlg.ContainerName, imageReference.Repository, imageReference.Tag,
                                        dlg.Ports, dlg.EnvVars, dlg.Volumes, dlg.RestartPolicy, dlg.MemoryMb, dlg.CpuCount, snapX, snapY,
                                        dlg.SelectedNetwork, dlg.Command, dlg.IsInteractive,
                                        dlg.RegUser, dlg.RegPass, dlg.RegServer);
                                }
                                else if (dlg.SelectedCreationMode == 1) // 2. [💻 명령어로 생성 (CLI)] 탭
                                {
                                    await vm.ProcessCliCommandAsync(dlg.CliCommand, snapX, snapY);
                                }
                                else if (dlg.SelectedCreationMode == 2) // 3. [🛠️ 도커파일로 빌드] 탭
                                {
                                    // defaultX, defaultY -> snapX, snapY 로 변경!
                                    await vm.BuildImageAndCreateNodeAsync(dlg.BuildImageTag, dlg.DockerfileContent, dlg.UploadedDockerfilePath, snapX, snapY);
                                }
                            }
                            catch (Exception ex)
                            {
                                _dialogService.ShowError($"에러 : {ex.Message}", "오류");
                            }
                            finally { Mouse.OverrideCursor = null; }
                        }
                    }
                    else if (d is DockerVolume volume)
                    {
                        if (volume.Name == "New Volume")
                        {
                            var dlg = new Views.VolumeDialog(_dialogService);
                            dlg.Owner = this;
                            if (dlg.ShowDialog() == true)
                            {
                                await vm.CreateNewVolumeNodeAsync(dlg.CreateOptions, snapX, snapY);
                            }
                        }
                        else
                        {
                            await vm.CreateNodeAtAsync(volume, snapX, snapY);
                        }
                    }
                    else if (d is DockerInternet internet)
                    {
                        await vm.CreateNodeAtAsync(new DockerInternet { Name = "Internet" }, snapX, snapY);
                    }
                }
                else // 기존 아이템 (이미 도커에 존재하는 객체를 화면에만 꺼내는 경우)
                {
                    if (d is DockerContainer existingContainer)
                    {
                        // 도커 상태값에 맞춰 노드 상태 색상을 갱신
                        if (string.Equals(existingContainer.State, "running", StringComparison.OrdinalIgnoreCase)) existingContainer.StateColor = "#28a745";
                        else if (string.Equals(existingContainer.State, "exited", StringComparison.OrdinalIgnoreCase) || string.Equals(existingContainer.State, "dead", StringComparison.OrdinalIgnoreCase)) existingContainer.StateColor = "#dc3545";
                        else existingContainer.StateColor = "#808080";
                    }
                    await vm.CreateNodeAtAsync(d, snapX, snapY);
                }
            }
            // [CASE B] 네트워크 그룹 (DockerGroupObject)
            else if (e.Data.GetDataPresent("DockerGroupObject"))
            {
                var group = e.Data.GetData("DockerGroupObject") as DockerNetworkGroup;
                if (group != null)
                {
                    await vm.CreateNodeAtAsync(group, snapX, snapY);
                    needsLayerUpdate = true;
                }
            }

            if (needsLayerUpdate && vm.ActiveSheet != null)
            {
                vm.ActiveSheet.UpdateGroupLayering();
            }

            RefreshKubernetesRelationships(vm.ActiveSheet);
        }

        private void RefreshKubernetesRelationships(SheetViewModel? sheet)
        {
            if (sheet?.RuntimeKind != RuntimeKind.Kubernetes)
                return;

            new KubernetesRelationshipService().RefreshRelationships(sheet);
        }

        /// <summary>
        /// 사이드바의 '실제 도커 리소스(컨테이너, 볼륨, 네트워크)' 목록에서 항목을 마우스로 드래그할 때 호출됩니다.
        /// 선택된 항목의 타입에 따라 알맞은 모델 데이터를 `DataObject`로 포장하여 시스템의 드래그 앤 드롭 파이프라인에 전달합니다.
        /// </summary>
        private void ExistingItem_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isToolDragging)
            {
                if (Math.Abs(e.GetPosition(null).X - _toolStartPoint.X) > 5)
                {
                    _isToolDragging = true;
                    var border = sender as Border;

                    // 1. 컨테이너인 경우
                    if (border?.DataContext is DockerContainer container)
                    {
                        DataObject data = new DataObject("DockerContainerObject", container);
                        DragDrop.DoDragDrop(border, data, DragDropEffects.Copy);
                    }
                    // 2. 볼륨인 경우 (이 부분이 누락되어 드래그가 안 됐던 것입니다)
                    else if (border?.DataContext is DockerVolume volume)
                    {
                        // Canvas_Drop에서 리소스를 통합 판단하므로 키를 "DockerContainerObject"로 동일하게 맞춥니다.
                        DataObject data = new DataObject("DockerContainerObject", volume);
                        DragDrop.DoDragDrop(border, data, DragDropEffects.Copy);
                    }
                    // 3. 네트워크(그룹)인 경우
                    else if (border?.DataContext is DockerNetworkGroup group)
                    {
                        DataObject data = new DataObject("DockerGroupObject", group);
                        DragDrop.DoDragDrop(border, data, DragDropEffects.Copy);
                    }

                }
            }
        }

        /// <summary>
        /// 사이드바의 '템플릿' 목록에서 항목을 드래그할 때 호출됩니다.
        /// 해당 템플릿의 이미지 정보를 바탕으로 신규 컨테이너 생성을 위한 데이터를 준비하여 드래그를 시작합니다.
        /// </summary>
        // 기존 템플릿 아이템 드래그
        private void TemplateItem_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isToolDragging)
            {
                if (Math.Abs(e.GetPosition(null).X - _toolStartPoint.X) > 5)
                {
                    _isToolDragging = true;
                    var border = sender as Border;
                    if (border != null && border.DataContext is TemplateItem template)
                    {
                        var container = new DockerContainer
                        {
                            Name = template.Name,
                            Image = template.Image,
                        };

                        DataObject data = new DataObject("DockerContainerObject", container);
                        DragDrop.DoDragDrop(border, data, DragDropEffects.Copy);

                        _isToolDragging = false;
                    }
                }
            }
        }

        /// <summary>
        /// 기존 도커 리소스 항목 드래그 시작 전, 마우스 클릭 지점을 기억하여 미세한 흔들림으로 인한 오작동을 방지(드래그 임계값 계산용)합니다.
        /// </summary>
        private void ExistingItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _toolStartPoint = e.GetPosition(null);
            _isToolDragging = false;
        }

        private async void ExistingItem_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isToolDragging)
            {
                _isToolDragging = false;
                return;
            }

            if (DataContext is not MainViewModel vm ||
                vm.ActiveSheet?.RuntimeKind != RuntimeKind.DockerEngine ||
                sender is not FrameworkElement element)
            {
                return;
            }

            if (element.DataContext is DockerContainer container)
            {
                Point placement = GetViewportCenteredPlacement(160, 80);
                await vm.CreateNodeAtAsync(container, placement.X, placement.Y);
                vm.Explorer.UpdateAvailableItems();
                e.Handled = true;
            }
            else if (element.DataContext is DockerVolume volume)
            {
                Point placement = GetViewportCenteredPlacement(160, 80);
                await vm.CreateNodeAtAsync(volume, placement.X, placement.Y);
                vm.Explorer.UpdateAvailableItems();
                e.Handled = true;
            }
            else if (element.DataContext is DockerNetworkGroup network)
            {
                _pendingExistingNetwork = network;
                _isGroupingMode = false;
                _isNetworkDrawingMode = true;
                Mouse.OverrideCursor = Cursors.Cross;
                vm.Inspector.ClearSelection();
                e.Handled = true;
            }
        }
        private void ComposeProject_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _toolStartPoint = e.GetPosition(null);
            _isComposeProjectDragging = false;
        }

        private void ComposeProject_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _isComposeProjectDragging) return;

            Point current = e.GetPosition(null);
            if (Math.Abs(current.X - _toolStartPoint.X) <= SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - _toolStartPoint.Y) <= SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            if (sender is FrameworkElement element &&
                element.DataContext is DockerComposeProject project)
            {
                _isComposeProjectDragging = true;
                DragDrop.DoDragDrop(
                    element,
                    new DataObject("ComposeProjectObject", project),
                    DragDropEffects.Copy);
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Input,
                    new Action(() => _isComposeProjectDragging = false));
            }
        }

        private async void ComposeProject_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isComposeProjectDragging)
            {
                _isComposeProjectDragging = false;
                return;
            }

            if (sender is not FrameworkElement element ||
                element.DataContext is not DockerComposeProject project)
            {
                return;
            }

            Point viewportCenter = GetViewportWorldCenter();
            await ViewModel.PlaceComposeProjectAsync(
                project,
                viewportCenter.X,
                viewportCenter.Y,
                centerOnPoint: true);
            e.Handled = true;
        }
        /// <summary>
        /// 템플릿 항목 드래그 시작 전 클릭 지점을 기록합니다.
        /// </summary>
        private void TemplateItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _toolStartPoint = e.GetPosition(null);
            _isToolDragging = false;
        }

        private void StackTemplate_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _toolStartPoint = e.GetPosition(null);
            _isStackTemplateDragging = false;
        }

        private void StackTemplate_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _isStackTemplateDragging) return;

            Point current = e.GetPosition(null);
            if (Math.Abs(current.X - _toolStartPoint.X) <= SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - _toolStartPoint.Y) <= SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            if (sender is FrameworkElement element &&
                element.DataContext is StackTemplateDefinition template)
            {
                _isStackTemplateDragging = true;
                DragDrop.DoDragDrop(
                    element,
                    new DataObject("StackTemplateObject", template),
                    DragDropEffects.Copy);
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Input,
                    new Action(() => _isStackTemplateDragging = false));
            }
        }

        private async void StackTemplate_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isStackTemplateDragging)
            {
                _isStackTemplateDragging = false;
                return;
            }

            if (sender is not FrameworkElement element ||
                element.DataContext is not StackTemplateDefinition template)
            {
                return;
            }

            Point placement = GetViewportCenteredPlacement(640, 360);

            await ShowStackTemplateDialogAndApplyAsync(
                template,
                placement.X,
                placement.Y);
        }

        private async Task ShowStackTemplateDialogAndApplyAsync(
            StackTemplateDefinition template,
            double x,
            double y)
        {
            string suggestedProjectName = template.DefaultProjectName;
            if (ViewModel.ActiveSheet != null)
            {
                suggestedProjectName = await StackTemplateDeploymentService.SuggestProjectNameAsync(
                    template,
                    ViewModel.ActiveSheet,
                    ViewModel.ActiveSheet.DockerService);
            }

            var dialog = new Views.StackTemplateDialog(template, suggestedProjectName)
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true) return;
            await ViewModel.ApplyStackTemplateAsync(template, dialog.DeploymentOptions, x, y);
        }

        /// <summary>
        /// 노드나 네트워크 그룹 객체의 모서리(크기 조절 그립)를 마우스로 눌렀을 때 호출됩니다.
        /// 리사이징 모드를 활성화하고 원본 크기와 초기 좌표를 기록하여, MouseMove 이벤트에서 부드럽게 크기를 변환할 수 있도록 합니다.
        /// </summary>
    }
}
