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
        // --- 스크롤 로직 ---

        /// <summary>
        /// 상단 멀티 탭(시트) 목록이 화면을 넘어갈 정도로 길어질 경우, 좌/우 화살표 버튼을 클릭해 탭을 부드럽게 스크롤하는 로직입니다.
        /// </summary>
        private void ScrollLeft_Click(object sender, RoutedEventArgs e)
        {
            double newOffset = Math.Max(0, TabScrollViewer.HorizontalOffset - SCROLL_OFFSET);
            TabScrollViewer.ScrollToHorizontalOffset(newOffset);
        }

        private void ScrollRight_Click(object sender, RoutedEventArgs e)
        {
            double newOffset = Math.Min(TabScrollViewer.ScrollableWidth, TabScrollViewer.HorizontalOffset + SCROLL_OFFSET);
            TabScrollViewer.ScrollToHorizontalOffset(newOffset);
        }

        private void AddContextButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SheetManager.IsWorkspaceLayer)
            {
                BtnSettings_Click(sender, e);
            }
            else
            {
                ViewModel.SheetManager.AddSheet();
            }
        }

        private void WorkspaceListBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var hit = VisualTreeHelper.HitTest(WorkspaceListBox, e.GetPosition(WorkspaceListBox))?.VisualHit;
            var item = FindParent<ListBoxItem>(hit, _ => true);
            if (item?.DataContext is ConnectionWorkspaceViewModel workspace)
            {
                ViewModel.SheetManager.EnterWorkspace(workspace);
                TabScrollViewer.ScrollToHorizontalOffset(0);
                e.Handled = true;
            }
        }

        private void WorkspaceCrumb_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SheetManager.ShowWorkspaceLayer();
            TabScrollViewer.ScrollToHorizontalOffset(0);
        }

        private void ConnectionManagerButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new Views.ConnectionManagerWindow(ViewModel, _dialogService)
            {
                Owner = this
            };
            window.ShowDialog();
        }

        /// <summary>
        /// 시트 탭에서 마우스 우클릭 -> [시트 비우기] 메뉴를 선택했을 때 호출됩니다.
        /// 선택한 시트의 모든 객체를 지우기 전 사용자에게 확인을 받습니다.
        /// </summary>
        private void ClearSheet_Click(object sender, RoutedEventArgs e)
        {
            // 우클릭한 메뉴 아이템에서 데이터(SheetViewModel)를 가져옴
            if (sender is MenuItem menuItem && menuItem.DataContext is SheetViewModel targetSheet)
            {
                // 확인 메시지 띄우기
                bool isConfirmed = _dialogService.ShowConfirm(
                    $"'{targetSheet.Title}' 시트의 모든 내용을 지우시겠습니까?\n(컨테이너, 연결선, 그룹이 모두 삭제됩니다)", "시트 비우기");

                if (isConfirmed)
                {
                    // 해당 시트의 내용물만 싹 비움
                    targetSheet.Nodes.Clear();
                    targetSheet.Connectors.Clear();
                    targetSheet.Groups.Clear();
                }
            }
        }

        /// <summary>
        /// 툴바 상단의 프로필/설정 버튼을 클릭했을 때 호출됩니다.
        /// 원격 서버에 SSH 터널링을 통해 새로운 도커 엔진 탭을 연결하는 다이얼로그를 띄웁니다.
        /// </summary>
        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var sshDlg = new Views.SshConnectionDialog(this.ViewModel, _dialogService);
            sshDlg.Owner = this;
            sshDlg.ShowDialog();
        }

        private void TabScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateScrollButtonsState();
        private void TabScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateScrollButtonsState();

        /// <summary>
        /// 탭 스크롤 위치에 따라 좌/우 이동 화살표 버튼의 활성화(Enable) 상태를 갱신합니다.
        /// </summary>
        private void UpdateScrollButtonsState()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                bool isScrollable = TabScrollViewer.ScrollableWidth > 0;
                bool isAtStart = TabScrollViewer.HorizontalOffset <= 2.0;
                bool isAtEnd = TabScrollViewer.HorizontalOffset >= TabScrollViewer.ScrollableWidth - 2.0;

                if (BtnScrollLeft != null) BtnScrollLeft.IsEnabled = !isAtStart;
                if (BtnScrollRight != null) BtnScrollRight.IsEnabled = !isAtEnd;
            }));
        }

        // --- 기타 UI 이벤트들 ---

        /// <summary>
        /// 그룹의 헤더(상단 제목 영역)를 마우스로 눌렀을 때, 그룹 전체 이동(드래그) 모드를 활성화합니다.
        /// </summary>
        private void GroupHeader_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement border) return;
            _movingGroup = border.DataContext as GroupViewModel;

            if (_movingGroup != null)
            {
                _isGroupMoving = true;
                Point mouseWorld = GetWorldPosition(e);
                _groupClickOffset = new Point(mouseWorld.X - _movingGroup.X, mouseWorld.Y - _movingGroup.Y);
                _groupMoveStartRect = new Rect(_movingGroup.X, _movingGroup.Y, _movingGroup.Width, _movingGroup.Height);
                border.CaptureMouse();
                e.Handled = true;
            }
        }

        /// <summary>
        /// 그룹의 본문(배경 영역)을 클릭했을 때 해당 그룹을 선택 상태로 전환합니다.
        /// </summary>
        private void GroupBody_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var rect = sender as FrameworkElement;
            var groupVm = rect?.DataContext as GroupViewModel;
            var vm = DataContext as MainViewModel;
            if (vm != null && groupVm != null)
            {
                vm.Inspector.SelectedElement = groupVm;
            }
            e.Handled = true;
        }

        /// <summary>
        /// 연결선의 양 끝에 위치한 그립(원형 핸들)을 클릭했을 때, 
        /// 기존 선을 떼어내어 다른 대상에게 연결하는 '재연결(Reconnection)' 모드를 시작합니다.
        /// </summary>
        private void ConnectorGrip_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Ellipse ellipse) return;
            if (ellipse.DataContext is not ConnectorViewModel connVm) return;
            _reconnectingConn = connVm;

            if (ellipse.Tag is string tag) _reconnectType = tag;
            else _reconnectType = string.Empty;

            _isReconnecting = true;
            TempPolyline.Visibility = Visibility.Visible;
            TempPolyline.Points = _reconnectingConn.Points;

            ellipse.CaptureMouse();
            e.Handled = true;
        }

        /// <summary>
        /// 상단 다이어그램 탭(시트)을 마우스로 클릭했을 때, 탭 순서를 변경하기 위한 드래그 앤 드롭 준비 상태에 돌입합니다.
        /// </summary>
        private void SheetTab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _sheetDragStartPoint = e.GetPosition(null);
            _isSheetDragging = false;
            _isClickedOnTab = true;
        }

        /// <summary>
        /// 탭을 클릭한 상태로 일정 거리 이상 이동하면 드래그 앤 드롭 이벤트를 발생시켜 탭의 순서 변경 작업을 시작합니다.
        /// </summary>
        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            base.OnPreviewMouseMove(e);
            if (e.LeftButton == MouseButtonState.Pressed && !_isSheetDragging && _isClickedOnTab)
            {
                var point = e.GetPosition(null);
                // 마우스가 클릭된 상태에서 10픽셀 이상 움직이면 드래그로 간주
                if (Math.Abs(point.X - _sheetDragStartPoint.X) > 10 || Math.Abs(point.Y - _sheetDragStartPoint.Y) > 10)
                {
                    var hitElem = VisualTreeHelper.HitTest(SheetListBox, e.GetPosition(SheetListBox))?.VisualHit;
                    var listBoxItem = FindParent<ListBoxItem>(hitElem, x => true);
                    if (listBoxItem != null && listBoxItem.DataContext is SheetViewModel sheet)
                    {
                        _isSheetDragging = true;
                        DragDrop.DoDragDrop(SheetListBox, new DataObject("SheetData", sheet), DragDropEffects.Move);
                        _isSheetDragging = false;
                        _isClickedOnTab = false;
                    }
                }
            }
        }

        /// <summary>
        /// 드래그 중인 탭을 다른 탭 위에 떨어뜨렸을 때(Drop), 뷰모델에 순서 변경(MoveSheet)을 요청하여 UI에 반영합니다.
        /// </summary>
        private void SheetTab_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("SheetData"))
            {
                var sourceSheet = e.Data.GetData("SheetData") as SheetViewModel;
                var vm = DataContext as MainViewModel;
                var pos = e.GetPosition(SheetListBox);
                var result = VisualTreeHelper.HitTest(SheetListBox, pos);

                if (result != null && sourceSheet != null && vm != null)
                {
                    var targetItem = FindParent<ListBoxItem>(result.VisualHit, x => true);
                    if (targetItem != null && targetItem.DataContext is SheetViewModel targetSheet)
                    {
                        int oldIdx = vm.Sheets.IndexOf(sourceSheet!);
                        int newIdx = vm.Sheets.IndexOf(targetSheet);
                        vm.SheetManager.MoveSheet(oldIdx, newIdx);
                    }
                }
            }
        }

        /// <summary>
        /// 시트 이름 변경(Rename) 팝업을 띄우는 이벤트 핸들러입니다.
        /// 시트 탭의 컨텍스트 메뉴에서 호출되며, 현재 시트의 이름을 텍스트 박스에 로드하고 포커스를 줍니다.
        /// </summary>
        private void RenameSheet_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var contextMenu = menuItem?.Parent as ContextMenu;
            var border = contextMenu?.PlacementTarget as FrameworkElement;
            if (border?.DataContext is SheetViewModel sheet)
            {
                _renamingSheet = sheet;
                _renamingWorkspace = null;
                txtRenameTitle.Text = "Rename Sheet";
                txtRename.Text = sheet.Title;
                RenameOverlay.Visibility = Visibility.Visible;
                txtRename.Focus();
                txtRename.SelectAll();
            }
        }

        private void RenameWorkspace_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var contextMenu = menuItem?.Parent as ContextMenu;
            var border = contextMenu?.PlacementTarget as FrameworkElement;
            if (border?.DataContext is ConnectionWorkspaceViewModel workspace)
            {
                _renamingWorkspace = workspace;
                _renamingSheet = null;
                txtRenameTitle.Text = "Rename Connection";
                txtRename.Text = workspace.DisplayName;
                RenameOverlay.Visibility = Visibility.Visible;
                txtRename.Focus();
                txtRename.SelectAll();
            }
        }

        private void ChangeWorkspaceRuntime_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem ||
                menuItem.CommandParameter is not string runtimeText ||
                !Enum.TryParse(runtimeText, out RuntimeKind targetRuntime))
            {
                return;
            }

            var workspace = ResolveWorkspaceFromMenu(menuItem);
            if (workspace == null) return;

            if (workspace.RuntimeKind == targetRuntime)
            {
                _dialogService.ShowInfo($"이미 {workspace.RuntimeLabel} runtime입니다.", "Runtime");
                return;
            }

            string targetLabel = GetRuntimeLabel(targetRuntime);
            bool confirmed = _dialogService.ShowConfirm(
                $"'{workspace.DisplayName}' 연결에 새 {targetLabel} Workspace와 시트를 만듭니다.\n기존 {workspace.RuntimeLabel} 시트는 그대로 유지됩니다.",
                "Runtime 변경");
            if (!confirmed) return;

            ViewModel.SheetManager.CreateRuntimeWorkspace(workspace, targetRuntime, activate: true);
            TabScrollViewer.ScrollToHorizontalOffset(0);
        }

        private ConnectionWorkspaceViewModel? ResolveWorkspaceFromMenu(MenuItem menuItem)
        {
            object? parent = menuItem.Parent;
            while (parent is MenuItem parentMenu)
                parent = parentMenu.Parent;

            if (parent is ContextMenu contextMenu)
            {
                if (contextMenu.DataContext is ConnectionWorkspaceViewModel contextWorkspace)
                    return contextWorkspace;

                if (contextMenu.PlacementTarget is FrameworkElement { DataContext: ConnectionWorkspaceViewModel placementWorkspace })
                    return placementWorkspace;
            }

            return ViewModel.SheetManager.ActiveWorkspace;
        }

        private static string GetRuntimeLabel(RuntimeKind runtimeKind) => runtimeKind switch
        {
            RuntimeKind.DockerEngine => "Docker",
            RuntimeKind.DockerSwarm => "Swarm",
            RuntimeKind.Kubernetes => "Kubernetes",
            _ => runtimeKind.ToString()
        };

        /// <summary>
        /// 시트 이름 변경을 확정(OK)하고 팝업을 닫습니다.
        /// </summary>
        private void RenameOK_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(txtRename.Text))
            {
                var sheetManager = (DataContext as MainViewModel)?.SheetManager;
                if (_renamingSheet != null)
                {
                    sheetManager?.RenameSheet(_renamingSheet, txtRename.Text.Trim());
                }
                else if (_renamingWorkspace != null)
                {
                    sheetManager?.RenameWorkspace(_renamingWorkspace, txtRename.Text.Trim());
                }
            }
            RenameOverlay.Visibility = Visibility.Collapsed;
            _renamingSheet = null;
            _renamingWorkspace = null;
        }

        /// <summary>
        /// 시트 이름 변경을 취소하고 팝업을 닫습니다.
        /// </summary>
        private void RenameCancel_Click(object sender, RoutedEventArgs e)
        {
            RenameOverlay.Visibility = Visibility.Collapsed;
            _renamingSheet = null;
            _renamingWorkspace = null;
        }

        /// <summary>
        /// 시트 탭의 컨텍스트 메뉴에서 시트 삭제를 클릭했을 때 호출됩니다.
        /// </summary>
        private void DeleteSheet_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var contextMenu = menuItem?.Parent as ContextMenu;
            var border = contextMenu?.PlacementTarget as FrameworkElement;
            if (border?.DataContext is SheetViewModel sheet)
            {
                (DataContext as MainViewModel)?.SheetManager.DeleteSheet(sheet);
            }
        }

        private void DeleteWorkspace_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var contextMenu = menuItem?.Parent as ContextMenu;
            var border = contextMenu?.PlacementTarget as FrameworkElement;
            if (border?.DataContext is not ConnectionWorkspaceViewModel workspace) return;

            if (workspace.Profile.Type == EndpointType.Local &&
                workspace.RuntimeKind == RuntimeKind.DockerEngine)
            {
                _dialogService.ShowMessage("Local PC 연결은 삭제할 수 없습니다.");
                return;
            }

            if (_dialogService.ShowConfirm($"'{workspace.DisplayName}' 연결을 삭제하시겠습니까?\n이 연결 안의 시트도 목록에서 제거됩니다.", "Connection Delete"))
            {
                (DataContext as MainViewModel)?.SheetManager.RemoveWorkspace(workspace);
            }
        }

        /// <summary>
        /// 탭 영역 오른쪽에 위치한 전체 시트 목록 보기 드롭다운 버튼을 클릭했을 때 호출됩니다.
        /// </summary>
        private void SheetMenuButton_Click(object sender, RoutedEventArgs e) => SheetMenuPopup.IsOpen = true;

        /// <summary>
        /// 전체 시트 목록 드롭다운에서 특정 시트를 선택하면, 즉시 해당 시트로 이동하고 팝업을 닫습니다.
        /// </summary>
        private void SheetMenuList_SelectionChanged(object sender, SelectionChangedEventArgs e) => SheetMenuPopup.IsOpen = false;

        /// <summary>
        /// 시트가 변경될 때, 탭 스크롤 영역에서 선택된 탭이 화면에 보이도록(ScrollIntoView) 스크롤을 자동 조절합니다.
        /// </summary>
        private void SheetListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SheetListBox.SelectedItem != null) SheetListBox.ScrollIntoView(SheetListBox.SelectedItem);
        }

        /// <summary>
        /// 왼쪽 사이드바 도구(Tool)를 마우스로 눌렀을 때 호출됩니다.
        /// 사용자가 버튼을 클릭한 것인지, 아니면 드래그 앤 드롭을 시작하려는 것인지 구분하기 위해 시작 좌표를 기록합니다.
        /// 네트워크나 그룹 도구의 경우, 캔버스 그리기 모드로 즉시 전환하고 이벤트를 소비합니다.
        /// </summary>
        // 1. 사이드바 아이콘 클릭 (네트워크면 그리기 모드 진입)
    }
}
