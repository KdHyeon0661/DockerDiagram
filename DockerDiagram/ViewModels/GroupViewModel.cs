using DockerDiagram.Helpers;
using DockerDiagram.Models;
using DockerDiagram.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;

namespace DockerDiagram.ViewModels
{
    /// <summary>
    /// 캔버스 위에서 여러 노드를 묶어주는 **그룹(Group)** 화면 요소를 관리하는 뷰모델 클래스입니다.
    /// 단순히 시각적인 정리를 위한 '일반 폴더(General)' 역할뿐만 아니라, 컨테이너들을 묶어 
    /// 실제 도커 통신망을 구성하고 제어하는 **'도커 네트워크(Network)'**의 핵심 역할도 함께 수행합니다.
    /// </summary>
    public class GroupViewModel : ViewModelBase, IConnectableItem
    {
        #region Fields & Services
        private readonly INetworkService _networkService;
        private readonly IDialogService _dialogService;

        private string _title = "Group";
        private bool _isSelected;
        private int _zIndex;
        private double _x, _y, _width, _height;

        private string _borderColor = "#555";
        private string _headerColor = "White";
        private string _headerFontColor = "#333";
        private double _strokeThickness = 2;
        private DoubleCollection? _strokeDashArray = new DoubleCollection { 4, 2 };
        #endregion

        #region Basic Properties
        public string Id { get; set; } = string.Empty;
        public string Title { get => _title; set => SetProperty(ref _title, value); }
        public string Name => Title; // 인터페이스의 Name을 Group의 Title로 연결
        public GroupType Type { get; }
        public string Driver { get; set; } = "bridge"; // 네트워크 드라이버 정보
        public int ZIndex { get => _zIndex; set => SetProperty(ref _zIndex, value); }
        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
        #endregion

        #region Layout Properties
        public double X
        {
            get => _x;
            set
            {
                if (SetProperty(ref _x, value))
                {
                    OnModified?.Invoke(this, EventArgs.Empty);
                    OnPositionChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public double Y
        {
            get => _y;
            set
            {
                if (SetProperty(ref _y, value))
                {
                    OnModified?.Invoke(this, EventArgs.Empty);
                    OnPositionChanged?.Invoke(this, EventArgs.Empty);
                }
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
                    OnPositionChanged?.Invoke(this, EventArgs.Empty);
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
                    OnPositionChanged?.Invoke(this, EventArgs.Empty);
                    ParentSheet?.UpdateGroupLayering();
                }
            }
        }

        public double CenterX => X + (Width / 2);
        public double CenterY => Y + (Height / 2);
        public double Area => Width * Height;

        // 부모 시트 (실행 순서 계산용)
        public SheetViewModel? ParentSheet { get; set; }
        #endregion

        #region Design Properties
        public string BorderColor { get => _borderColor; set => SetProperty(ref _borderColor, value); }
        public string HeaderColor { get => _headerColor; set => SetProperty(ref _headerColor, value); }
        public string HeaderFontColor { get => _headerFontColor; set => SetProperty(ref _headerFontColor, value); }
        public double StrokeThickness { get => _strokeThickness; set => SetProperty(ref _strokeThickness, value); }
        public DoubleCollection? StrokeDashArray { get => _strokeDashArray; set => SetProperty(ref _strokeDashArray, value); }
        #endregion

        #region Collections
        // 포함된 노드들
        public ObservableCollection<NodeViewModel> ContainedNodes { get; } = new();
        #endregion

        #region Commands
        public ICommand ArrangeCommand { get; }
        public ICommand StartAllCommand { get; }
        public ICommand StopAllCommand { get; }
        #endregion

        #region Events
        public event EventHandler? OnModified;
        public event EventHandler? OnPositionChanged;
        #endregion

        #region Constructor
        /// <summary>
        /// 지정된 위치와 크기, 타입(일반/네트워크)을 바탕으로 새로운 그룹 객체를 생성하고 초기화합니다.
        /// </summary>
        // ★ [수정] 생성자에서 그룹의 타입을 확정 지어 넘겨받습니다.
        public GroupViewModel(double x, double y, double w, double h,
                              INetworkService networkService,
                              IDialogService dialogService,
                              string title = "New Group",
                              GroupType type = GroupType.General) // 기본값은 일반 폴더
        {
            _networkService = networkService;
            _dialogService = dialogService;

            X = x; Y = y; Width = w; Height = h; Title = title;
            Type = type; // 타입 확정!

            ArrangeCommand = new RelayCommand(_ => ArrangeNodes());
            StartAllCommand = new AsyncRelayCommand(StartAllContainers);
            StopAllCommand = new AsyncRelayCommand(StopAllContainers);

            // 타입이 확정되었으니, 그에 맞는 옷(색상)을 입혀줍니다.
            UpdateAppearance();
        }
        #endregion

        #region Appearance Methods
        /// <summary>
        /// 그룹의 타입(일반 폴더 또는 네트워크)에 맞춰 테두리 색상, 배경색, 점선/실선 등의 시각적 디자인을 자동으로 변경합니다.
        /// </summary>
        private void UpdateAppearance()
        {
            if (Type == GroupType.Network)
            {
                BorderColor = "#9B59B6"; // 보라색 실선
                HeaderColor = "#9B59B6";
                HeaderFontColor = "White";
                StrokeThickness = 2;
                StrokeDashArray = null;
            }
            else
            {
                BorderColor = "#555"; // 회색 점선
                HeaderColor = "White";
                HeaderFontColor = "#333";
                StrokeThickness = 2;
                StrokeDashArray = new DoubleCollection { 4, 2 };
            }
        }
        #endregion

        #region Node Management Methods
        /// <summary>
        /// 이 그룹 안에 특정 노드(컨테이너 등)를 포함시킵니다. 
        /// 만약 이 그룹이 **'네트워크' 타입**이라면, 도커 엔진과 통신하여 실제 컨테이너를 해당 네트워크 망에 즉시 연결(Connect)합니다.
        /// </summary>
        public async Task AddNodeAsync(NodeViewModel node, bool isRestoring = false)
        {
            if (!ContainedNodes.Contains(node))
            {
                ContainedNodes.Add(node);
                OnModified?.Invoke(this, EventArgs.Empty);

                if (!isRestoring && Type == GroupType.Network && !string.IsNullOrEmpty(node.ContainerId))
                {
                    if (ParentSheet?.Profile.Type == EndpointType.Local && !DockerServiceHelper.IsDockerRunning()) return;

                    try
                    {
                        await _networkService.ConnectNetworkAsync(this.Title, node.ContainerId);
                    }
                    catch (Exception ex)
                    {
                        // 1. 도커 엔진 특성상 무시 가능한 에러 (이미 연결된 상태)
                        if (ex.Message.Contains("이미 연결") || ex.Message.Contains("already") || ex.Message.Contains("in use"))
                        {
                            Debug.WriteLine($"[DockerDiscovery] {node.Name}은(는) 이미 {this.Title} 네트워크에 연결되어 있습니다.");
                        }
                        // 2. 🚨 진짜 통신 에러 발생 시 (UI 알림 후 상위로 전파)
                        else
                        {
                            Debug.WriteLine($"[DockerDiscovery] 네트워크 연결 실패: {ex.Message}");
                            _dialogService.ShowError($"'{node.Name}' 컨테이너를 '{this.Title}' 네트워크에 연결하는 중 오류가 발생했습니다.\n{ex.Message}", "Network Error");
                            throw; // AsyncRelayCommand나 최상위 이벤트 핸들러가 이 예외를 캐치할 수 있도록 던짐
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 이 그룹에서 특정 노드를 빼냅니다.
        /// 만약 이 그룹이 **'네트워크' 타입**이라면, 도커 엔진과 통신하여 실제 컨테이너를 해당 네트워크 망에서 즉시 분리(Disconnect)합니다.
        /// </summary>
        public async Task RemoveNodeAsync(NodeViewModel node, bool isRestoring = false) // 💡 Aysnc -> Async 오타 수정 완료
        {
            if (ContainedNodes.Contains(node))
            {
                ContainedNodes.Remove(node);
                OnModified?.Invoke(this, EventArgs.Empty);

                if (!isRestoring && Type == GroupType.Network && !string.IsNullOrEmpty(node.ContainerId))
                {
                    if (ParentSheet?.Profile.Type == EndpointType.Local && !DockerServiceHelper.IsDockerRunning()) return;

                    try
                    {
                        await _networkService.DisconnectNetworkAsync(this.Title, node.ContainerId);
                    }
                    catch (Exception ex)
                    {
                        // 1. 도커 엔진 특성상 무시 가능한 에러 (애초에 연결되어 있지 않은 상태)
                        if (ex.Message.Contains("is not connected") || ex.Message.Contains("연결되어 있지"))
                        {
                            Debug.WriteLine($"[DockerDiscovery] {node.Name}은(는) 원래 {this.Title} 네트워크에 없습니다.");
                        }
                        // 2. 🚨 진짜 통신 에러 발생 시 (UI 알림 후 상위로 전파)
                        else
                        {
                            Debug.WriteLine($"[DockerDiscovery] 네트워크 해제 실패: {ex.Message}");
                            _dialogService.ShowError($"'{node.Name}' 컨테이너를 '{this.Title}' 네트워크에서 해제하는 중 오류가 발생했습니다.\n{ex.Message}", "Network Error");
                            throw; // AsyncRelayCommand나 최상위 이벤트 핸들러가 이 예외를 캐치할 수 있도록 던짐
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 사용자가 마우스로 그룹 전체를 드래그할 때, 그룹 자체의 위치뿐만 아니라 내부에 포함된 모든 자식 노드들의 위치도 함께 이동시킵니다.
        /// </summary>
        public void MoveBy(double dx, double dy)
        {
            X += dx;
            Y += dy;

            foreach (var node in ContainedNodes)
            {
                node.X += dx;
                node.Y += dy;
            }
        }
        #endregion

        #region Arrangement & Execution Methods
        /// <summary>
        /// 그룹 내부에 포함된 노드와 하위 그룹들을 겹치지 않게 트리(Tree) 구조로 분석하여,
        /// 지정된 열(Column) 개수에 맞춰 예쁘게 바둑판 배열로 자동 정렬합니다.
        /// </summary>
        private void ArrangeNodes()
        {
            if (ContainedNodes.Count == 0) return;

            var dlg = new Views.ArrangeDialog(_dialogService);
            if (App.Current.MainWindow != null)
                dlg.Owner = App.Current.MainWindow;

            if (dlg.ShowDialog() == true)
            {
                int cols = dlg.Columns;

                double padding = 20;
                double headerHeight = 20;
                double gap = 20;
                double nestIndent = 20;

                double maxNodeW = ContainedNodes.Any() ? ContainedNodes.Max(n => n.Width) : 100;
                double maxNodeH = ContainedNodes.Any() ? ContainedNodes.Max(n => n.Height) : 50;

                var allGroups = new List<GroupViewModel> { this };
                if (ParentSheet != null)
                {
                    // 현재 그룹 안에 완전히 포함된 하위 그룹(자식 그룹)들 찾기
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

                int maxDepth = 0;
                void CalcMaxDepth(GroupViewModel g, int currentDepth)
                {
                    maxDepth = Math.Max(maxDepth, currentDepth);
                    foreach (var child in groupTree[g]) CalcMaxDepth(child, currentDepth + 1);
                }
                CalcMaxDepth(this, 0); // 재귀 호출로 트리 최대 깊이 계산

                double globalNodeGridWidth = (cols * maxNodeW) + (gap * (cols - 1));
                double globalNodeStartX = this.X + padding + (maxDepth * nestIndent);

                double LayoutTree(GroupViewModel currentGroup, double startY, int depth)
                {
                    int depthFromBottom = maxDepth - depth;

                    currentGroup.X = globalNodeStartX - padding - (depthFromBottom * nestIndent);
                    currentGroup.Y = startY;
                    currentGroup.Width = globalNodeGridWidth + (padding * 2) + (depthFromBottom * nestIndent * 2);

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

                    if (hasElements)
                    {
                        currentYPos -= gap;
                    }

                    currentGroup.Height = currentYPos - startY + padding;

                    return currentGroup.Y + currentGroup.Height + gap;
                }

                // 최상위 루트 노드부터 재귀 정렬 시작
                LayoutTree(this, this.Y, 0);

                this.Width = Math.Max(this.Width, 150);
                this.Height = Math.Max(this.Height, 100);

                OnModified?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// 그룹에 포함된 모든 컨테이너 노드를 의존성(선 연결) 순서에 맞춰서 순차적으로 시작(Start)합니다.
        /// </summary>
        private async Task StartAllContainers()
        {
            var executionOrder = GetExecutionOrder();
            if (executionOrder == null || executionOrder.Count == 0) return;

            foreach (var node in executionOrder)
            {
                if (node.IsRunning) continue;
                if (node.StartCommand.CanExecute(null))
                {
                    await node.StartCommand.ExecuteAsync(null);
                    await Task.Delay(1000); // 컨테이너 간 시작 안정화 시간(1초) 부여
                }
            }
            _dialogService.ShowMessage("전체 실행 완료");
        }

        /// <summary>
        /// 그룹에 포함된 모든 컨테이너 노드를 시작할 때의 역순으로 안전하게 정지(Stop)시킵니다.
        /// </summary>
        private async Task StopAllContainers()
        {
            var executionOrder = GetExecutionOrder();
            if (executionOrder == null || executionOrder.Count == 0) return;

            executionOrder.Reverse(); // 정지는 시작의 역순으로!

            foreach (var node in executionOrder)
            {
                if (!node.IsRunning) continue;
                if (node.StopCommand.CanExecute(null))
                {
                    await node.StopCommand.ExecuteAsync(null);
                    await Task.Delay(500); // 정지는 비교적 빠르게(0.5초 간격)
                }
            }
            _dialogService.ShowMessage("전체 정지 완료");
        }

        /// <summary>
        /// 위상 정렬(Topological Sort) 알고리즘을 사용하여 컨테이너 간의 실행 순서(의존성 트리)를 계산합니다.
        /// </summary>
        private List<NodeViewModel>? GetExecutionOrder()
        {
            if (ParentSheet == null) return null;

            var containers = ContainedNodes.Where(n => n.Type == NodeType.Container).ToList();
            if (containers.Count == 0) return null;

            var dependencies = new Dictionary<NodeViewModel, List<NodeViewModel>>();
            var inDegree = new Dictionary<NodeViewModel, int>(); // 진입 차수 (나를 가리키는 화살표 개수)

            foreach (var c in containers)
            {
                dependencies[c] = new List<NodeViewModel>();
                inDegree[c] = 0;
            }

            foreach (var conn in ParentSheet.Connectors)
            {
                if (conn.Source is NodeViewModel sourceNode && conn.Target is NodeViewModel targetNode)
                {
                    // 그룹 안의 컨테이너들끼리의 연결선만 분석
                    if (containers.Contains(sourceNode) && containers.Contains(targetNode))
                    {
                        dependencies[targetNode].Add(sourceNode);
                        inDegree[sourceNode]++; // Target에 의존하므로 Source의 진입 차수 증가
                    }
                }
            }

            var queue = new Queue<NodeViewModel>();
            // 진입 차수가 0인 (아무에게도 의존하지 않는 가장 밑바닥) 노드부터 큐에 삽입
            foreach (var c in containers) { if (inDegree[c] == 0) queue.Enqueue(c); }

            var order = new List<NodeViewModel>();
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                order.Add(current);

                // 현재 노드에 의존하던 다음 노드들의 차수를 깎고, 0이 되면 큐에 삽입
                foreach (var dependent in dependencies[current])
                {
                    inDegree[dependent]--;
                    if (inDegree[dependent] == 0) queue.Enqueue(dependent);
                }
            }

            // 혹시 선이 연결 안 돼서 큐에 못 들어갔던 독립적인 노드들 추가
            foreach (var c in containers) { if (!order.Contains(c)) order.Add(c); }
            return order;
        }
        #endregion
    }
}
