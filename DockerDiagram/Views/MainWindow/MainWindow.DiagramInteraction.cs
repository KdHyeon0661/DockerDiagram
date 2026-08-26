using DockerDiagram.Diagram;
using DockerDiagram.Contracts;
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
        /// <summary>
        /// 다이어그램 캔버스 위에서 마우스 휠을 굴렸을 때 발생하는 이벤트를 가로채어 화면을 줌 인/줌 아웃(확대/축소) 처리합니다.
        /// </summary>
        private void Diagram_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (DataContext is not MainViewModel vm ||
                vm.ActiveSheet is not SheetViewModel sheet)
            {
                return;
            }

            e.Handled = true;

            Point mousePosition = e.GetPosition(ViewportCanvas);
            double oldScale = sheet.Scale;
            double zoomFactor = e.Delta > 0 ? 1.1 : 0.9;
            double newScale = Math.Min(
                SheetViewModel.MaximumScale,
                oldScale * zoomFactor);

            if (Math.Abs(newScale - oldScale) < 0.0001)
                return;

            double worldX = (mousePosition.X - sheet.OffsetX) / oldScale;
            double worldY = (mousePosition.Y - sheet.OffsetY) / oldScale;

            sheet.Scale = newScale;
            sheet.OffsetX = mousePosition.X - (worldX * newScale);
            sheet.OffsetY = mousePosition.Y - (worldY * newScale);
            CaptureActiveSheetViewportCenter(markModified: false);
            vm.SheetManager.MarkAsModified();
        }

        /// <summary>
        /// 캔버스 빈 공간을 마우스 우클릭으로 눌렀을 때, 화면 전체를 이동시키는 패닝(Panning) 모드를 시작하고 마우스를 캡처합니다.
        /// </summary>
        private void Diagram_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isPanning = true;
            _panStartClick = e.GetPosition(this);
            _panStartTranslate = new Point(MapTranslate.X, MapTranslate.Y);
            Mouse.OverrideCursor = Cursors.SizeAll;
            ZoomPanGrid.CaptureMouse();
        }

        /// <summary>
        /// 우클릭을 떼었을 때 패닝(Panning) 모드를 종료하고, 이전 도구 상태(그리기 모드 등)에 맞춰 마우스 커서를 복구합니다.
        /// </summary>
        private void Diagram_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                ZoomPanGrid.ReleaseMouseCapture();
                CaptureActiveSheetViewportCenter(markModified: true);
                // 모드에 따라 커서 복구
                if (_isGroupingMode || _isNetworkDrawingMode) Mouse.OverrideCursor = Cursors.Cross;
                else Mouse.OverrideCursor = null;
            }
        }

        /// <summary>
        /// 캔버스 내 마우스 좌클릭 이벤트입니다. 
        /// 사용자가 '그룹/네트워크 그리기 모드'인 경우 클릭한 지점부터 영역 생성을 시작하고(자식 요소로 이벤트 전파 차단),
        /// 일반 모드인 경우 빈 배경을 클릭했는지를 판별하여 현재 선택된 요소를 해제(ClearSelection)합니다.
        /// </summary>
        // 3. 캔버스 클릭 (네트워크/그룹 그리기 시작)
        private void Diagram_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 1. [그리기 모드] (그룹 또는 네트워크)
            // -> 기존 요소(그룹/노드)가 클릭 이벤트를 받기 전에 가로채서 그리기를 시작합니다.
            if (_isGroupingMode || _isNetworkDrawingMode)
            {
                // 화면 좌표 계산
                _startPoint = e.GetPosition(ZoomPanGrid);

                // XAML에 미리 만들어둔 임시 사각형(TempGroupRect) 재사용 및 초기화
                if (TempGroupRect != null)
                {
                    TempGroupRect.Visibility = Visibility.Visible;
                    TempGroupRect.Width = 0;
                    TempGroupRect.Height = 0;
                    Canvas.SetLeft(TempGroupRect, _startPoint.X);
                    Canvas.SetTop(TempGroupRect, _startPoint.Y);

                    if (_isGroupingMode)
                    {
                        // 그룹 스타일 (노란색 점선)
                        TempGroupRect.Stroke = Brushes.Orange;
                        TempGroupRect.StrokeDashArray = new DoubleCollection { 4, 2 };
                        TempGroupRect.Fill = new SolidColorBrush(Color.FromArgb(30, 255, 165, 0));
                    }
                    else if (_isNetworkDrawingMode)
                    {
                        // 네트워크 스타일 (보라색 점선)
                        TempGroupRect.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9B59B6"));
                        TempGroupRect.StrokeDashArray = new DoubleCollection { 2, 2 };
                        TempGroupRect.Fill = new SolidColorBrush(Color.FromArgb(30, 155, 89, 182));
                    }
                }

                // 마우스 가두기 및 이벤트 종료(자식에게 전파 금지)
                ViewportCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            // 2. [일반 모드] 배경 클릭 체크
            // Preview 이벤트는 노드를 클릭해도 실행되므로, 클릭한 대상이 '진짜 배경'인지 확인해야 합니다.
            var clickedElement = e.OriginalSource as DependencyObject;

            // 클릭한 것이 캔버스 배경(ZoomPanGrid)이나 ViewportCanvas, 또는 배경용 흰색 Rectangle인 경우
            if (clickedElement == ZoomPanGrid || clickedElement == ViewportCanvas ||
               (clickedElement is System.Windows.Shapes.Rectangle rect && rect.Fill == Brushes.White))
            {
                (DataContext as MainViewModel)?.Inspector.ClearSelection();
            }

            // 주의: 일반 모드일 때는 e.Handled = true를 하지 않습니다.
            // 그래야 노드를 클릭했을 때 노드 선택/드래그 로직이 정상 작동합니다.
        }

        /// <summary>
        /// 캔버스 위에서 마우스가 움직일 때 발생하는 모든 드래그 관련 액션을 총괄하는 핵심 라우팅 메서드입니다.
        /// 현재 활성화된 상태 플래그에 따라 '영역 그리기', '화면 패닝', '그룹 이동', '크기 조절(리사이징)', '선 긋기(연결/재연결)' 등 
        /// 적절한 시각적 피드백을 실시간으로 렌더링합니다.
        /// </summary>
        // 4. 캔버스 드래그 (네트워크 사각형 크기 조절 포함)
        private void Diagram_MouseMove(object sender, MouseEventArgs e)
        {
            // 1. [최우선] 그리기 모드 (그룹 또는 네트워크 생성 중)
            if (ViewportCanvas.IsMouseCaptured && (_isGroupingMode || _isNetworkDrawingMode))
            {
                if (TempGroupRect == null) return;

                var currentPoint = e.GetPosition(ZoomPanGrid);
                double x = Math.Min(_startPoint.X, currentPoint.X);
                double y = Math.Min(_startPoint.Y, currentPoint.Y);
                double w = Math.Abs(_startPoint.X - currentPoint.X);
                double h = Math.Abs(_startPoint.Y - currentPoint.Y);

                Canvas.SetLeft(TempGroupRect, x);
                Canvas.SetTop(TempGroupRect, y);
                TempGroupRect.Width = w;
                TempGroupRect.Height = h;

                return;
            }

            var vm = DataContext as MainViewModel;
            if (vm?.ActiveSheet == null) return;

            Point current = GetWorldPosition(e);

            // 2. 화면 패닝 (우클릭 드래그)
            if (_isPanning)
            {
                Point currentMouse = e.GetPosition(this);
                double diffX = currentMouse.X - _panStartClick.X;
                double diffY = currentMouse.Y - _panStartClick.Y;
                vm.ActiveSheet.OffsetX = _panStartTranslate.X + diffX;
                vm.ActiveSheet.OffsetY = _panStartTranslate.Y + diffY;
                return;
            }

            // 3. 그룹 전체 이동
            if (_isGroupMoving && _movingGroup != null)
            {
                double rawTargetX = current.X - _groupClickOffset.X;
                double rawTargetY = current.Y - _groupClickOffset.Y;

                double snappedTargetX = Math.Round(rawTargetX / 10.0) * 10.0;
                double snappedTargetY = Math.Round(rawTargetY / 10.0) * 10.0;

                double dx = snappedTargetX - _movingGroup.X;
                double dy = snappedTargetY - _movingGroup.Y;

                if (Math.Abs(dx) >= 1 || Math.Abs(dy) >= 1)
                {
                    _movingGroup.MoveBy(dx, dy);
                }
                return;
            }

            // 4. 노드 또는 그룹 크기 조절
            if (_isResizing)
            {
                double diffX = current.X - _resizeStartWorldPos.X;
                double diffY = current.Y - _resizeStartWorldPos.Y;

                if (_resizingNode != null)
                {
                    if (_resizeDir.Contains("Right"))
                    {
                        _resizingNode.Width = Math.Max(50, _resizeStartNodeRect.Width + diffX);
                    }
                    if (_resizeDir.Contains("Bottom"))
                    {
                        _resizingNode.Height = Math.Max(50, _resizeStartNodeRect.Height + diffY);
                    }
                    if (_resizeDir.Contains("Left"))
                    {
                        double w = _resizeStartNodeRect.Width - diffX;
                        if (w >= 50)
                        {
                            _resizingNode.X = _resizeStartNodeRect.X + diffX;
                            _resizingNode.Width = w;
                        }
                    }
                    if (_resizeDir.Contains("Top"))
                    {
                        double h = _resizeStartNodeRect.Height - diffY;
                        if (h >= 50)
                        {
                            _resizingNode.Y = _resizeStartNodeRect.Y + diffY;
                            _resizingNode.Height = h;
                        }
                    }
                }
                else if (_resizingGroup != null)
                {
                    Rect contentBounds = GetGroupContentBounds(_resizingGroup);
                    double padding = 20;

                    if (_resizeDir.Contains("Right"))
                    {
                        double minW = _resizingGroup.ContainedNodes.Count > 0
                            ? Math.Max(GroupViewModel.MinimumWidth, (contentBounds.Right - _resizeStartGroupRect.X) + padding)
                            : GroupViewModel.MinimumWidth;
                        _resizingGroup.Width = Math.Max(minW, _resizeStartGroupRect.Width + diffX);
                    }
                    if (_resizeDir.Contains("Bottom"))
                    {
                        double minH = _resizingGroup.ContainedNodes.Count > 0
                            ? Math.Max(GroupViewModel.MinimumHeight, (contentBounds.Bottom - _resizeStartGroupRect.Y) + padding)
                            : GroupViewModel.MinimumHeight;
                        _resizingGroup.Height = Math.Max(minH, _resizeStartGroupRect.Height + diffY);
                    }
                    if (_resizeDir.Contains("Left"))
                    {
                        double maxAllowedX = _resizingGroup.ContainedNodes.Count > 0
                            ? Math.Min(contentBounds.Left - padding, _resizeStartGroupRect.Right - GroupViewModel.MinimumWidth)
                            : _resizeStartGroupRect.Right - GroupViewModel.MinimumWidth;
                        double cX = Math.Min(_resizeStartGroupRect.X + diffX, maxAllowedX);
                        _resizingGroup.X = cX;
                        _resizingGroup.Width = _resizeStartGroupRect.Right - cX;
                    }
                    if (_resizeDir.Contains("Top"))
                    {
                        double maxAllowedY = _resizingGroup.ContainedNodes.Count > 0
                            ? Math.Min(contentBounds.Top - padding, _resizeStartGroupRect.Bottom - GroupViewModel.MinimumHeight)
                            : _resizeStartGroupRect.Bottom - GroupViewModel.MinimumHeight;
                        double cY = Math.Min(_resizeStartGroupRect.Y + diffY, maxAllowedY);
                        _resizingGroup.Y = cY;
                        _resizingGroup.Height = _resizeStartGroupRect.Bottom - cY;
                    }
                }
                return;
            }

            // 5. 재연결 대상 위에서는 실제 연결선과 동일한 자동 라우팅 결과를 미리 보여줍니다.
            if (_isReconnecting && _reconnectingConn != null)
            {
                Point startPoint;
                Point endPoint;
                Rect sourceBounds;
                Rect targetBounds;
                PortDirection sourceDirection;
                PortDirection targetDirection;

                if (_reconnectType == "Source")
                {
                    startPoint = current;
                    endPoint = _reconnectingConn.TargetPos;
                    sourceDirection = PortDirection.None;
                    targetDirection = _reconnectingConn.TargetDir;
                    sourceBounds = new Rect(current.X, current.Y, 0, 0);
                    targetBounds = _reconnectingConn.Target.UsePointRouting
                        ? new Rect(endPoint.X, endPoint.Y, 0, 0)
                        : new Rect(
                            _reconnectingConn.Target.X,
                            _reconnectingConn.Target.Y,
                            _reconnectingConn.Target.Width,
                            _reconnectingConn.Target.Height);
                }
                else
                {
                    startPoint = _reconnectingConn.SourcePos;
                    endPoint = current;
                    sourceDirection = _reconnectingConn.SourceDir;
                    targetDirection = PortDirection.None;
                    sourceBounds = _reconnectingConn.Source.UsePointRouting
                        ? new Rect(startPoint.X, startPoint.Y, 0, 0)
                        : new Rect(
                            _reconnectingConn.Source.X,
                            _reconnectingConn.Source.Y,
                            _reconnectingConn.Source.Width,
                            _reconnectingConn.Source.Height);
                    targetBounds = new Rect(current.X, current.Y, 0, 0);
                }

                var hitResult = VisualTreeHelper.HitTest(ZoomPanGrid, e.GetPosition(ZoomPanGrid));
                var hitElement = hitResult == null
                    ? null
                    : FindParent<FrameworkElement>(hitResult.VisualHit, element => element.DataContext is IConnectableItem);
                if (hitElement?.DataContext is IConnectableItem hoverItem)
                {
                    IConnectableItem routeSource = _reconnectType == "Source"
                        ? hoverItem
                        : _reconnectingConn.Source;
                    IConnectableItem routeTarget = _reconnectType == "Source"
                        ? _reconnectingConn.Target
                        : hoverItem;
                    TempPolyline.Points = ConnectorRoutePlanner.Calculate(routeSource, routeTarget).Points;
                    return;
                }

                try
                {
                    PointCollection route = OrthogonalRouter.GetRoute(
                        startPoint,
                        sourceDirection,
                        endPoint,
                        targetDirection,
                        sourceBounds,
                        targetBounds);
                    TempPolyline.Points = route.Count >= 2
                        ? route
                        : new PointCollection { startPoint, endPoint };
                }
                catch
                {
                    TempPolyline.Points = new PointCollection { startPoint, endPoint };
                }
                return;
            }
            // 6. 대상 위에서는 실제 연결선과 동일한 자동 라우팅 결과를 미리 보여줍니다.
            if (_isConnecting && _sourceItem != null)
            {
                Point startPoint = ConnectorRoutePlanner.GetBorderPoint(_sourceItem, _sourceDir);
                Point endPoint = current;
                Rect sourceBounds = _sourceItem.UsePointRouting
                    ? new Rect(startPoint.X, startPoint.Y, 0, 0)
                    : new Rect(_sourceItem.X, _sourceItem.Y, _sourceItem.Width, _sourceItem.Height);
                Rect targetBounds = new Rect(current.X, current.Y, 0, 0);

                if (_lastHitPort != null)
                {
                    _lastHitPort.Background = Brushes.Transparent;
                    _lastHitPort = null;
                }

                var hitResult = VisualTreeHelper.HitTest(ZoomPanGrid, e.GetPosition(ZoomPanGrid));
                var hitElement = hitResult == null
                    ? null
                    : FindParent<FrameworkElement>(hitResult.VisualHit, element => element.DataContext is IConnectableItem);
                if (hitElement?.DataContext is IConnectableItem targetItem && targetItem != _sourceItem)
                {
                    Rect targetRect = new Rect(targetItem.X, targetItem.Y, targetItem.Width, targetItem.Height);
                    PortDirection targetDirection = GetClosestDirection(current, targetRect);
                    TempPolyline.Points = ConnectorRoutePlanner.Calculate(_sourceItem, targetItem).Points;
                    return;
                }

                try
                {
                    PointCollection route = OrthogonalRouter.GetRoute(
                        startPoint,
                        _sourceDir,
                        endPoint,
                        PortDirection.None,
                        sourceBounds,
                        targetBounds);
                    TempPolyline.Points = route.Count >= 2
                        ? route
                        : new PointCollection { startPoint, endPoint };
                }
                catch
                {
                    TempPolyline.Points = new PointCollection { startPoint, endPoint };
                }
            }
        }
        /// <summary>
        /// 캔버스 위에서 마우스 왼쪽 버튼을 뗐을 때 호출되는 최종 이벤트 핸들러입니다.
        /// 드래그로 진행 중이던 시각적 상호작용(영역 그리기, 노드/그룹 이동, 연결선 긋기, 리사이징 등)을 확정하고,
        /// 계산된 최종 좌표나 상태를 뷰모델(ViewModel)의 비즈니스 로직에 반영하여 실제 데이터 모델을 업데이트합니다.
        /// </summary>
        // 5. 마우스 뗌 (네트워크/그룹 생성 완료)
        private async void Diagram_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // 최상위 이벤트 핸들러이므로 내부 비동기 예외를 여기서 처리합니다.
            try
            {
                _isClickedOnTab = false;

                // 1. [그리기 모드 완료] (그룹 또는 네트워크)
                if (ViewportCanvas.IsMouseCaptured && (_isGroupingMode || _isNetworkDrawingMode))
                {
                    ViewportCanvas.ReleaseMouseCapture();

                    // 임시 도형 숨기기
                    if (TempGroupRect != null) TempGroupRect.Visibility = Visibility.Collapsed;
                    if (TempPolyline != null) TempPolyline.Visibility = Visibility.Collapsed;

                    var vm = DataContext as MainViewModel;

                    // 임시 사각형이 있고 ViewModel이 연결된 경우 생성 로직 실행
                    if (vm != null && TempGroupRect != null)
                    {
                        double w = TempGroupRect.Width;
                        double h = TempGroupRect.Height;
                        double x = Canvas.GetLeft(TempGroupRect);
                        double y = Canvas.GetTop(TempGroupRect);

                        // 너무 작게(단순 클릭) 한 경우 생성 방지 (최소 20x20)
                        if (w > 20 && h > 20)
                        {
                            // A) 일반 그룹 생성
                            if (_isGroupingMode && vm.ActiveSheet?.DockerService is INetworkService netService)
                            {
                                var newGroup = new GroupViewModel(x, y, w, h, netService, _dialogService);
                                newGroup.ParentSheet = vm.ActiveSheet;
                                vm.ActiveSheet.Groups.Add(newGroup);

                                await vm.ActiveSheet.RefreshGroupContainmentAsync(newGroup);

                                vm.ActiveSheet.UpdateGroupLayering();
                            }
                            // B) 도커 네트워크 생성
                            else if (_isNetworkDrawingMode)
                            {
                                if (_pendingExistingNetwork != null)
                                {
                                    await vm.CreateExistingNetworkGroupAsync(
                                        _pendingExistingNetwork,
                                        x,
                                        y,
                                        w,
                                        h);
                                    vm.Explorer.UpdateAvailableItems();
                                }
                                else
                                {
                                    var dlg = new Views.NetworkDialog(_dialogService);
                                    dlg.Owner = this;
                                    if (dlg.ShowDialog() == true)
                                    {
                                        await vm.CreateNewNetworkGroupAsync(dlg.CreateOptions, x, y, w, h);
                                    }
                                }
                            }
                        }
                    }

                    // 모드 초기화 및 커서 복구
                    _isGroupingMode = false;
                    _isNetworkDrawingMode = false;
                    _pendingExistingNetwork = null;
                    Mouse.OverrideCursor = null;

                    e.Handled = true;
                    return;
                }

                // --- 기존 기능들 ---

                // 2. 그룹 이동 종료
                if (_isGroupMoving)
                {
                    if (_movingGroup != null && DataContext is MainViewModel vm)
                    {
                        var after = new Rect(_movingGroup.X, _movingGroup.Y, _movingGroup.Width, _movingGroup.Height);
                        vm.RecordGroupRectChange(_movingGroup, _groupMoveStartRect, after, $"Move group {_movingGroup.Title}");
                    }
                    _isGroupMoving = false;
                    Mouse.Capture(null);
                    _movingGroup = null;
                }

                // 3. 노드 드래그 종료
                if (_isNodeDragging)
                {
                    if (_draggedNodeElement != null && _draggedNodeElement.DataContext is NodeViewModel nodeVm)
                    {
                        var sheet = (DataContext as MainViewModel)?.ActiveSheet;
                        if (sheet != null)
                        {
                            // 이동이 끝난 노드의 위치를 기반으로 어떤 그룹 영역에 포함되는지 검사하여 자동 소속 처리
                            var targetGroups = sheet.FindGroupsAt(nodeVm.X, nodeVm.Y, nodeVm.Width, nodeVm.Height);

                            foreach (var group in sheet.Groups)
                            {
                                if (targetGroups.Contains(group))
                                {
                                    await group.AddNodeAsync(nodeVm);
                                }
                                else
                                {
                                    await group.RemoveNodeAsync(nodeVm);
                                }
                            }
                        }
                        if (DataContext is MainViewModel historyVm)
                        {
                            var after = new Rect(nodeVm.X, nodeVm.Y, nodeVm.Width, nodeVm.Height);
                            historyVm.RecordNodeRectChange(nodeVm, _nodeDragStartRect, after, $"Move node {nodeVm.Name}");
                        }
                        _draggedNodeElement.ReleaseMouseCapture();
                    }
                    _isNodeDragging = false;
                    _draggedNodeElement = null;
                }

                // 4. 재연결 종료 (IConnectableItem 적용)
                if (_isReconnecting && _reconnectingConn != null)
                {
                    _isReconnecting = false;
                    (Mouse.Captured as FrameworkElement)?.ReleaseMouseCapture();
                    if (TempPolyline != null) TempPolyline.Visibility = Visibility.Collapsed;

                    // 히트 테스트 및 연결 업데이트
                    var hitResult = VisualTreeHelper.HitTest(ZoomPanGrid, e.GetPosition(ZoomPanGrid));
                    if (hitResult != null)
                    {
                        var hitNodeObj = FindParent<FrameworkElement>(hitResult.VisualHit, x => x.DataContext is IConnectableItem);
                        if (hitNodeObj != null && hitNodeObj.DataContext is IConnectableItem hitItem)
                        {
                            Rect nodeRect = new Rect(hitItem.X, hitItem.Y, hitItem.Width, hitItem.Height);
                            PortDirection newDir = GetClosestDirection(GetWorldPosition(e), nodeRect);

                            if (_reconnectType == "Source")
                                _reconnectingConn.UpdateConnection(hitItem, newDir, _reconnectingConn.Target, _reconnectingConn.TargetDir);
                            else
                                _reconnectingConn.UpdateConnection(_reconnectingConn.Source, _reconnectingConn.SourceDir, hitItem, newDir);
                        }
                    }
                    _reconnectingConn = null;
                    return;
                }

                // 5. 리사이징 종료
                if (_isResizing)
                {
                    _isResizing = false;
                    (Mouse.Captured as FrameworkElement)?.ReleaseMouseCapture();

                    if (_resizingGroup != null)
                    {
                        var vm = DataContext as MainViewModel;
                        vm?.ActiveSheet?.UpdateGroupLayering();
                        var after = new Rect(_resizingGroup.X, _resizingGroup.Y, _resizingGroup.Width, _resizingGroup.Height);
                        vm?.RecordGroupRectChange(_resizingGroup, _resizeStartGroupRect, after, $"Resize group {_resizingGroup.Title}");
                    }
                    else if (_resizingNode != null)
                    {
                        var vm = DataContext as MainViewModel;
                        var after = new Rect(_resizingNode.X, _resizingNode.Y, _resizingNode.Width, _resizingNode.Height);
                        vm?.RecordNodeRectChange(_resizingNode, _resizeStartNodeRect, after, $"Resize node {_resizingNode.Name}");
                    }

                    _resizingNode = null;
                    _resizingGroup = null;
                }

                // 6. 신규 연결 종료 (IConnectableItem 적용 및 _sourceItem 사용)
                if (_isConnecting)
                {
                    _isConnecting = false;
                    Mouse.Capture(null);
                    if (TempPolyline != null) TempPolyline.Visibility = Visibility.Collapsed;
                    if (_lastHitPort != null) { _lastHitPort.Background = System.Windows.Media.Brushes.Transparent; _lastHitPort = null; }

                    var hitResult = VisualTreeHelper.HitTest(ZoomPanGrid, e.GetPosition(ZoomPanGrid));
                    if (hitResult != null)
                    {
                        var hitNodeObj = FindParent<FrameworkElement>(hitResult.VisualHit, el => el.DataContext is IConnectableItem);
                        if (hitNodeObj != null && hitNodeObj.DataContext is IConnectableItem targetItem)
                        {
                            // 그룹은 IsCreating 속성이 없으므로, NodeViewModel일 때만 확인하도록 캐스팅 처리
                            bool isCreating = (targetItem as NodeViewModel)?.IsCreating ?? false;

                            if (!isCreating && targetItem != _sourceItem && _sourceItem != null)
                            {
                                Rect targetRect = new Rect(targetItem.X, targetItem.Y, targetItem.Width, targetItem.Height);
                                PortDirection targetDir = GetClosestDirection(GetWorldPosition(e), targetRect);

                                if (DataContext is MainViewModel mainViewModel)
                                {
                                    await mainViewModel.AddConnectionAsync(_sourceItem, targetItem, _sourceDir, targetDir);
                                }
                            }
                        }
                    }
                    _sourceItem = null;
                }
            }
            catch (Exception ex)
            {
                // 최상위 UI 핸들러 통신/로직 에러를 안전하게 방어하고 사용자에게 안내
                _dialogService.ShowError($"마우스 조작 완료 처리 중 에러가 발생했습니다:\n{ex.Message}", "UI Interaction Error");
            }
        }

        private void Resize_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var el = sender as FrameworkElement;
            if (el != null && el.Tag is string dir)
            {
                _isResizing = true;
                _resizeDir = dir;
                _resizeStartWorldPos = GetWorldPosition(e);

                if (el.DataContext is NodeViewModel nodeVm)
                {
                    _resizingNode = nodeVm;
                    _resizeStartNodeRect = new Rect(nodeVm.X, nodeVm.Y, nodeVm.Width, nodeVm.Height);
                }
                else if (el.DataContext is GroupViewModel groupVm)
                {
                    _resizingGroup = groupVm;
                    _resizeStartGroupRect = new Rect(groupVm.X, groupVm.Y, groupVm.Width, groupVm.Height);
                }

                el.CaptureMouse();
                e.Handled = true;
            }
        }

        /// <summary>
        /// 노드의 상/하/좌/우에 위치한 연결 포트(Port) 동그라미를 마우스로 눌렀을 때 호출됩니다.
        /// 다른 노드나 볼륨으로 이어지는 선(Connector) 긋기 모드를 시작하며, 아직 생성 중(Creating)인 불안정한 객체에서는 선을 그을 수 없도록 차단합니다.
        /// </summary>
        private void Port_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var port = sender as FrameworkElement;
            _sourceItem = port?.DataContext as IConnectableItem;
            if (port?.Tag is string dirStr) Enum.TryParse(dirStr, out _sourceDir);

            if (_sourceItem is NodeViewModel nv && nv.IsCreating)
            {
                _dialogService.ShowMessage("생성 중인 객체는 연결할 수 없습니다.");
                return;
            }

            if (_sourceItem != null)
            {
                _isConnecting = true;
                _startPointCanvas = ConnectorRoutePlanner.GetBorderPoint(_sourceItem, _sourceDir);
                TempPolyline.Visibility = Visibility.Visible;
                Mouse.Capture(ZoomPanGrid);
                e.Handled = true;
            }
        }

        /// <summary>
        /// 노드의 상단 헤더 영역을 마우스로 클릭했을 때, 해당 노드의 이동(드래그) 모드를 시작합니다.
        /// 마우스 커서와 노드 원점 간의 오프셋을 계산하여 자연스러운 드래그를 준비합니다.
        /// </summary>
        private void Node_Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_isConnecting || _isResizing) return;
            var b = sender as FrameworkElement;
            if (b != null)
            {
                var nodeVm = b.DataContext as NodeViewModel;
                _isNodeDragging = true;
                _draggedNodeElement = b;
                Point mouseWorld = GetWorldPosition(e);
                if (nodeVm != null)
                {
                    _nodeClickOffset = new Point(mouseWorld.X - nodeVm.X, mouseWorld.Y - nodeVm.Y);
                    _nodeDragStartRect = new Rect(nodeVm.X, nodeVm.Y, nodeVm.Width, nodeVm.Height);
                }
                b.CaptureMouse();
                e.Handled = true;
            }
        }

        /// <summary>
        /// 마우스를 드래그하는 동안 노드의 좌표를 실시간으로 갱신하여 화면에 반영합니다.
        /// </summary>
        private void Node_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isNodeDragging && _draggedNodeElement != null)
            {
                var vm = _draggedNodeElement.DataContext as NodeViewModel;
                Point mouseWorld = GetWorldPosition(e);
                if (vm != null) { vm.X = mouseWorld.X - _nodeClickOffset.X; vm.Y = mouseWorld.Y - _nodeClickOffset.Y; }
            }
        }

        /// <summary>
        /// 마우스 버튼을 떼어 노드 드래그를 종료합니다.
        /// (참고: 최종 드롭 위치에 따른 그룹 소속 판별 로직은 최상위 캔버스의 MouseUp 이벤트에서 일괄 처리됩니다.)
        /// </summary>
        private void Node_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isNodeDragging)
            {
                _isNodeDragging = false;
                _draggedNodeElement?.ReleaseMouseCapture();
                _draggedNodeElement = null;
            }
        }

        /// <summary>
        /// 노드의 본문(Body) 영역을 클릭했을 때, 해당 노드를 선택 상태(SelectedElement)로 전환하거나 이미 선택되어 있다면 해제합니다.
        /// 선택된 노드는 우측 사이드바에 상세 정보(Inspect)가 표시됩니다.
        /// </summary>
        private void Node_Body_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_isConnecting || _isResizing) return;

            var b = sender as FrameworkElement;
            var nodeVm = b?.DataContext as NodeViewModel;
            var mainVm = DataContext as MainViewModel;

            if (mainVm != null && nodeVm != null)
            {
                if (mainVm.Inspector.SelectedElement == nodeVm)
                    mainVm.Inspector.SelectedElement = null;
                else
                    mainVm.Inspector.SelectedElement = nodeVm;
            }
            e.Handled = true;
        }

        /// <summary>
        /// 캔버스에 그려진 연결선(Connector) 자체를 클릭했을 때, 선을 선택 상태로 전환합니다.
        /// 선택된 선은 Delete 키를 눌러 삭제할 수 있습니다.
        /// </summary>
        private void Connector_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var el = sender as FrameworkElement;
            var connVm = el?.DataContext as ConnectorViewModel;
            var mainVm = DataContext as MainViewModel;

            if (mainVm != null && connVm != null)
            {
                mainVm.Inspector.SelectedElement = connVm;
                e.Handled = true;
            }
        }
    }
}
