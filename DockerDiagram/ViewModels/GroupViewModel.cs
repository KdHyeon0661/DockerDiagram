using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DockerDiagram.Helpers;
using DockerDiagram.Models;
using DockerDiagram.Views;

namespace DockerDiagram.ViewModels
{
    public class GroupViewModel : ViewModelBase
    {
        // 서비스
        private readonly INetworkService _networkService;
        private readonly IDialogService _dialogService;

        public string Id { get; set; } = string.Empty;
        public string Driver { get; set; } = "bridge"; // 네트워크 드라이버 정보

        // 위치 및 크기
        private double _x, _y, _width, _height;
        private string _title = "Group";
        private bool _isSelected;

        private int _zIndex;
        public int ZIndex
        {
            get => _zIndex;
            set => SetProperty(ref _zIndex, value);
        }

        public double Area => Width * Height;

        public event EventHandler? OnModified;

        // 부모 시트 (실행 순서 계산용)
        public SheetViewModel? ParentSheet { get; set; }

        // 포함된 노드들
        public ObservableCollection<NodeViewModel> ContainedNodes { get; } = new();

        // 커맨드
        public ICommand ArrangeCommand { get; }
        public ICommand StartAllCommand { get; }
        public ICommand StopAllCommand { get; }


        // 1. [수정] 중복된 GroupType 삭제하고 'Type' 하나로 통일
        private GroupType _type = GroupType.General;
        public GroupType Type
        {
            get => _type;
            set
            {
                if (SetProperty(ref _type, value))
                {
                    UpdateAppearance();
                    OnModified?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        // --- 디자인 속성 ---
        private string _borderColor = "#555";
        public string BorderColor { get => _borderColor; set => SetProperty(ref _borderColor, value); }

        private string _headerColor = "White";
        public string HeaderColor { get => _headerColor; set => SetProperty(ref _headerColor, value); }

        private string _headerFontColor = "#333";
        public string HeaderFontColor { get => _headerFontColor; set => SetProperty(ref _headerFontColor, value); }

        private double _strokeThickness = 2;
        public double StrokeThickness { get => _strokeThickness; set => SetProperty(ref _strokeThickness, value); }

        private DoubleCollection? _strokeDashArray = new DoubleCollection { 4, 2 };
        public DoubleCollection? StrokeDashArray { get => _strokeDashArray; set => SetProperty(ref _strokeDashArray, value); }


        // --- 위치/크기 속성 (Setter 단순화) ---
        public double X
        {
            get => _x;
            set
            {
                if (SetProperty(ref _x, value)) OnModified?.Invoke(this, EventArgs.Empty);
            }
        }

        public double Y
        {
            get => _y;
            set
            {
                if (SetProperty(ref _y, value)) OnModified?.Invoke(this, EventArgs.Empty);
            }
        }

        public double Width
        {
            get => _width;
            set
            {
                if (SetProperty(ref _width, value))
                {
                    OnModified?.Invoke(this, EventArgs.Empty);
                    ParentSheet?.UpdateGroupLayering();
                }
            }
        }

        public double Height
        {
            get => _height;
            set
            {
                if (SetProperty(ref _height, value))
                {
                    OnModified?.Invoke(this, EventArgs.Empty);
                    ParentSheet?.UpdateGroupLayering();
                }
            }
        }

        public string Title
        {
            get => _title;
            set
            {
                if (SetProperty(ref _title, value)) OnModified?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        // --- 생성자 ---
        public GroupViewModel(double x, double y, double w, double h,
                              INetworkService networkService,
                              IDialogService dialogService,
                              string title = "New Group")
        {
            _networkService = networkService;
            _dialogService = dialogService;

            X = x; Y = y; Width = w; Height = h; Title = title;

            ArrangeCommand = new RelayCommand(_ => ArrangeNodes());
            StartAllCommand = new AsyncRelayCommand(StartAllContainers);
            StopAllCommand = new AsyncRelayCommand(StopAllContainers);

            UpdateAppearance();
        }

        private void UpdateAppearance()
        {
            if (Type == GroupType.Network)
            {
                BorderColor = "#9B59B6"; // 보라색
                HeaderColor = "#9B59B6";
                HeaderFontColor = "White";
                StrokeThickness = 2;
                StrokeDashArray = null; // 실선
            }
            else
            {
                BorderColor = "#555"; // 회색
                HeaderColor = "White";
                HeaderFontColor = "#333";
                StrokeThickness = 2;
                StrokeDashArray = new DoubleCollection { 4, 2 }; // 점선
            }
        }

        // --- 노드 추가/삭제 로직 (네트워크 연동 포함) ---

        public async void AddNode(NodeViewModel node)
        {
            if (!ContainedNodes.Contains(node))
            {
                ContainedNodes.Add(node);
                OnModified?.Invoke(this, EventArgs.Empty);

                // 2. [수정] 네트워크 타입이고, 유효한 ID가 있을 때만 연결 시도
                if (Type == GroupType.Network &&
                    !string.IsNullOrEmpty(node.ContainerId) &&
                    !string.IsNullOrEmpty(this.Id))
                {
                    try
                    {
                        // ★ Title(이름)이 아니라 Id(도커 ID)로 연결해야 안전함
                        await _networkService.ConnectNetworkAsync(this.Id, node.ContainerId);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Network Attach Fail: {ex.Message}");
                        _dialogService.ShowMessage($"네트워크 연결 실패: {ex.Message}");
                    }
                }
            }
        }

        public async void RemoveNode(NodeViewModel node)
        {
            if (ContainedNodes.Contains(node))
            {
                ContainedNodes.Remove(node);
                OnModified?.Invoke(this, EventArgs.Empty);

                // 2. [수정] 네트워크 연결 해제
                if (Type == GroupType.Network &&
                    !string.IsNullOrEmpty(node.ContainerId) &&
                    !string.IsNullOrEmpty(this.Id))
                {
                    try
                    {
                        // ★ Title 대신 Id 사용
                        await _networkService.DisconnectNetworkAsync(this.Id, node.ContainerId);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Network Detach Fail: {ex.Message}");
                    }
                }
            }
        }

        public void MoveBy(double dx, double dy)
        {
            // SetProperty를 타면서 OnModified는 자동으로 발생함
            X += dx;
            Y += dy;

            // 내부 노드들도 같이 이동
            foreach (var node in ContainedNodes)
            {
                node.X += dx;
                node.Y += dy;
            }
        }

        // --- 자동 정렬 및 실행 제어 (기존 로직 유지) ---

        private void ArrangeNodes()
        {
            if (ContainedNodes.Count == 0) return;

            var dlg = new ArrangeDialog();
            if (Application.Current.MainWindow != null)
                dlg.Owner = Application.Current.MainWindow;

            if (dlg.ShowDialog() == true)
            {
                int cols = dlg.Columns;

                double padding = 20;
                double headerHeight = 20;
                double gap = 20;
                double nestIndent = 20;

                double maxNodeW = ContainedNodes.Any() ? ContainedNodes.Max(n => n.Width) : 100;
                double maxNodeH = ContainedNodes.Any() ? ContainedNodes.Max(n => n.Height) : 50;

                // 1. 트리 구조 구축 
                var allGroups = new List<GroupViewModel> { this };
                if (ParentSheet != null)
                {
                    foreach (var g in ParentSheet.Groups)
                    {
                        if (g != this && g.ContainedNodes.Any() &&
                            g.ContainedNodes.All(n => this.ContainedNodes.Contains(n)))
                        {
                            allGroups.Add(g);
                        }
                    }
                }

                var nodeToGroup = new Dictionary<NodeViewModel, GroupViewModel>();
                foreach (var node in ContainedNodes)
                {
                    var parentGroup = allGroups
                        .Where(g => g.ContainedNodes.Contains(node))
                        .OrderBy(g => g.ContainedNodes.Count)
                        .First();
                    nodeToGroup[node] = parentGroup;
                }

                var groupTree = new Dictionary<GroupViewModel, List<GroupViewModel>>();
                foreach (var g in allGroups) { groupTree[g] = new List<GroupViewModel>(); }

                foreach (var g in allGroups)
                {
                    if (g == this) continue;
                    var parentGroup = allGroups
                        .Where(p => p != g && p.ContainedNodes.Count > g.ContainedNodes.Count &&
                                    g.ContainedNodes.All(n => p.ContainedNodes.Contains(n)))
                        .OrderBy(p => p.ContainedNodes.Count)
                        .FirstOrDefault() ?? this;

                    groupTree[parentGroup].Add(g);
                }

                // 2. 최대 깊이(Depth) 계산
                int maxDepth = 0;
                void CalcMaxDepth(GroupViewModel g, int currentDepth)
                {
                    maxDepth = Math.Max(maxDepth, currentDepth);
                    foreach (var child in groupTree[g]) CalcMaxDepth(child, currentDepth + 1);
                }
                CalcMaxDepth(this, 0);

                // 3. 진정한 바텀-업(Bottom-Up) 기반의 전역 좌표계
                double globalNodeGridWidth = (cols * maxNodeW) + (gap * (cols - 1));
                double globalNodeStartX = this.X + padding + (maxDepth * nestIndent);

                // 4. 재귀적 렌더링
                double LayoutTree(GroupViewModel currentGroup, double startY, int depth)
                {
                    int depthFromBottom = maxDepth - depth;

                    // ★ [수정1] 폭/X좌표뿐만 아니라 Y좌표도 명시적으로 업데이트!
                    currentGroup.X = globalNodeStartX - padding - (depthFromBottom * nestIndent);
                    currentGroup.Y = startY;
                    currentGroup.Width = globalNodeGridWidth + (padding * 2) + (depthFromBottom * nestIndent * 2);

                    // ★ [수정2] 헤더 아래에 padding을 더해 '제일 위의 것'이 천장에 붙지 않게 숨통을 터줌
                    double currentYPos = startY + headerHeight + padding;
                    bool hasElements = false;

                    foreach (var childGroup in groupTree[currentGroup])
                    {
                        currentYPos = LayoutTree(childGroup, currentYPos, depth + 1);
                        hasElements = true;
                    }

                    var directNodes = ContainedNodes.Where(n => nodeToGroup[n] == currentGroup).ToList();
                    if (directNodes.Any())
                    {
                        hasElements = true;
                        int col = 0;
                        foreach (var node in directNodes)
                        {
                            node.X = globalNodeStartX + (col * (maxNodeW + gap));
                            node.Y = currentYPos;

                            col++;
                            if (col >= cols)
                            {
                                col = 0;
                                currentYPos += maxNodeH + gap;
                            }
                        }
                        if (col > 0) currentYPos += maxNodeH + gap;
                    }

                    // ★ [수정3] 마지막 요소 뒤에 불필요하게 더해진 gap을 빼서 하단 여백을 상단과 대칭으로 맞춤
                    if (hasElements)
                    {
                        currentYPos -= gap;
                    }

                    currentGroup.Height = currentYPos - startY + padding;

                    // 다음 형제 요소를 위해 Y좌표 + 자신의 높이 + gap 반환
                    return currentGroup.Y + currentGroup.Height + gap;
                }

                LayoutTree(this, this.Y, 0);

                this.Width = Math.Max(this.Width, 150);
                this.Height = Math.Max(this.Height, 100);

                OnModified?.Invoke(this, EventArgs.Empty);
            }
        }

        private async Task StartAllContainers()
        {
            var executionOrder = GetExecutionOrder();
            if (executionOrder == null || executionOrder.Count == 0) return;

            foreach (var node in executionOrder)
            {
                if (node.IsRunning) continue;
                if (node.StartCommand.CanExecute(null))
                {
                    await node.StartCommand.ExecuteAsync(null); // Async 실행 권장
                    await Task.Delay(1000); // 딜레이
                }
            }
            _dialogService.ShowMessage("실행 완료");
        }

        private async Task StopAllContainers()
        {
            var executionOrder = GetExecutionOrder();
            if (executionOrder == null || executionOrder.Count == 0) return;

            executionOrder.Reverse();

            foreach (var node in executionOrder)
            {
                if (!node.IsRunning) continue;
                if (node.StopCommand.CanExecute(null))
                {
                    await node.StopCommand.ExecuteAsync(null);
                    await Task.Delay(500);
                }
            }
            _dialogService.ShowMessage("정지 완료");
        }

        // 위상 정렬 (의존성 순서) - 기존 로직 유지
        private List<NodeViewModel>? GetExecutionOrder()
        {
            if (ParentSheet == null) return null;

            var containers = ContainedNodes.Where(n => n.Type == NodeType.Container).ToList();
            if (containers.Count == 0) return null;

            var dependencies = new Dictionary<NodeViewModel, List<NodeViewModel>>();
            var inDegree = new Dictionary<NodeViewModel, int>();

            foreach (var c in containers)
            {
                dependencies[c] = new List<NodeViewModel>();
                inDegree[c] = 0;
            }

            // 시트 전체 연결선 중에서, 내 그룹 안에 있는 컨테이너 간의 연결만 확인
            foreach (var conn in ParentSheet.Connectors)
            {
                if (containers.Contains(conn.Source) && containers.Contains(conn.Target))
                {
                    dependencies[conn.Target].Add(conn.Source);
                    inDegree[conn.Source]++;
                }
            }

            var queue = new Queue<NodeViewModel>();
            foreach (var c in containers) { if (inDegree[c] == 0) queue.Enqueue(c); }

            var order = new List<NodeViewModel>();
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                order.Add(current);

                foreach (var dependent in dependencies[current])
                {
                    inDegree[dependent]--;
                    if (inDegree[dependent] == 0) queue.Enqueue(dependent);
                }
            }

            // 순환 참조 등으로 빠진 노드들 추가
            foreach (var c in containers) { if (!order.Contains(c)) order.Add(c); }
            return order;
        }
    }
}