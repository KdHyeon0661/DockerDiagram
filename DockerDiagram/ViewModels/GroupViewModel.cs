using DockerDiagram.Helpers;
using DockerDiagram.Models;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Diagnostics;
using System.Windows.Media;

namespace DockerDiagram.ViewModels
{
    public class GroupViewModel : ViewModelBase
    {
        private double _x, _y, _width, _height;
        private string _title = "Group";
        private bool _isSelected;

        private readonly INetworkService _networkService;
        private readonly IDialogService _dialogService;

        // 그룹 타입 (일반 vs 네트워크)
        private GroupType _type = GroupType.General;
        public GroupType Type
        {
            get => _type;
            set
            {
                if (SetProperty(ref _type, value))
                {
                    UpdateAppearance();
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


        public double X { get => _x; set { _x = value; OnPropertyChanged(); } }
        public double Y { get => _y; set { _y = value; OnPropertyChanged(); } }
        public double Width { get => _width; set { _width = value; OnPropertyChanged(); } }
        public double Height { get => _height; set { _height = value; OnPropertyChanged(); } }
        public string Title { get => _title; set { _title = value; OnPropertyChanged(); } }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public ObservableCollection<NodeViewModel> ContainedNodes { get; } = new();

        public ICommand ArrangeCommand { get; }
        public ICommand StartAllCommand { get; }
        public ICommand StopAllCommand { get; }

        public SheetViewModel? ParentSheet { get; set; }

        // ★ [수정] 생성자: INetworkService 주입
        public GroupViewModel(double x, double y, double w, double h,
                              INetworkService networkService, // <-- 변경됨
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

        private void ArrangeNodes()
        {
            if (ContainedNodes.Count == 0) return;
            // (정렬 로직은 그대로 유지 - View 레벨의 로직이거나 별도 알고리즘)
        }

        // --- Start/Stop All 로직 (변경 없음) ---
        // 각 노드의 StartCommand를 호출하므로, GroupVM이 직접 ContainerService를 알 필요가 없음
        private async Task StartAllContainers()
        {
            var executionOrder = GetExecutionOrder();
            if (executionOrder == null || executionOrder.Count == 0) return;

            foreach (var node in executionOrder)
            {
                if (node.IsRunning) continue;
                if (node.StartCommand.CanExecute(null))
                {
                    node.StartCommand.Execute(null);
                    await Task.Delay(1500);
                }
            }
            _dialogService.ShowMessage($"그룹 내 컨테이너 {executionOrder.Count}개를 순차적으로 실행했습니다.");
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
                    node.StopCommand.Execute(null);
                    await Task.Delay(500);
                }
            }
            _dialogService.ShowMessage($"그룹 내 컨테이너 {executionOrder.Count}개를 순차적으로 정지했습니다.");
        }

        public void MoveBy(double dx, double dy)
        {
            X += dx; Y += dy;
            foreach (var node in ContainedNodes) { node.X += dx; node.Y += dy; }
        }

        // ★ [수정] INetworkService 사용
        public async void AddNode(NodeViewModel node)
        {
            if (!ContainedNodes.Contains(node))
            {
                ContainedNodes.Add(node);

                // 네트워크 그룹이고, 노드가 실제 컨테이너(ID 있음)라면 연결
                if (Type == GroupType.Network && !string.IsNullOrEmpty(node.ContainerId))
                {
                    try
                    {
                        // DockerService -> _networkService로 변경
                        // Title을 Network ID/Name으로 사용한다고 가정
                        await _networkService.ConnectNetworkAsync(this.Title, node.ContainerId);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Network Attach Fail: {ex.Message}");
                    }
                }
            }
        }

        // ★ [수정] INetworkService 사용
        public async void RemoveNode(NodeViewModel node)
        {
            if (ContainedNodes.Contains(node))
            {
                ContainedNodes.Remove(node);

                // 네트워크 그룹이고, 노드가 실제 컨테이너라면 연결 해제
                if (Type == GroupType.Network && !string.IsNullOrEmpty(node.ContainerId))
                {
                    try
                    {
                        // DockerService -> _networkService로 변경
                        await _networkService.DisconnectNetworkAsync(this.Title, node.ContainerId);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Network Detach Fail: {ex.Message}");
                    }
                }
            }
        }

        private List<NodeViewModel>? GetExecutionOrder()
        {
            if (ParentSheet == null) return null;
            // 컨테이너 타입인 노드만 필터링 (Volume 등 제외)
            var containers = ContainedNodes.Where(n => n.Type == NodeType.Container).ToList();
            if (containers.Count == 0) return null;

            var dependencies = new Dictionary<NodeViewModel, List<NodeViewModel>>();
            var inDegree = new Dictionary<NodeViewModel, int>();

            foreach (var c in containers)
            {
                dependencies[c] = new List<NodeViewModel>();
                inDegree[c] = 0;
            }

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

            foreach (var c in containers) { if (!order.Contains(c)) order.Add(c); }
            return order;
        }
    }
}