using System.Windows;
using DockerDiagram.Helpers;
using DockerDiagram.Models;
using DockerDiagram.Views;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
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

        // ★ [핵심 추가] MainViewModel이 구독할 이벤트 정의
        public event EventHandler? OnModified;

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
                    OnModified?.Invoke(this, EventArgs.Empty); // 타입 변경 시 저장 필요
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

        // ★ [수정] 위치/크기 변경 시 OnModified 호출
        public double X
        {
            get => _x;
            set
            {
                if (_x != value)
                {
                    _x = value;
                    OnPropertyChanged();
                    OnModified?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public double Y
        {
            get => _y;
            set
            {
                if (_y != value)
                {
                    _y = value;
                    OnPropertyChanged();
                    OnModified?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public double Width
        {
            get => _width;
            set
            {
                if (_width != value)
                {
                    _width = value;
                    OnPropertyChanged();
                    OnModified?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public double Height
        {
            get => _height;
            set
            {
                if (_height != value)
                {
                    _height = value;
                    OnPropertyChanged();
                    OnModified?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string Title
        {
            get => _title;
            set
            {
                if (_title != value)
                {
                    _title = value;
                    OnPropertyChanged();
                    OnModified?.Invoke(this, EventArgs.Empty);
                }
            }
        }

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

        // 생성자: INetworkService 주입
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

        private void ArrangeNodes()
        {
            if (ContainedNodes.Count == 0) return;

            // 모달 띄우기
            var dlg = new ArrangeDialog();
            dlg.Owner = Application.Current.MainWindow;

            if (dlg.ShowDialog() == true)
            {
                int cols = dlg.Columns;
                double padding = 20;
                double headerHeight = 40;
                double gap = 15;

                // 1. 그리드 셀 크기 계산 (가장 큰 노드 기준)
                double maxNodeW = ContainedNodes.Max(n => n.Width);
                double maxNodeH = ContainedNodes.Max(n => n.Height);

                // 2. 배치 시작
                for (int i = 0; i < ContainedNodes.Count; i++)
                {
                    var node = ContainedNodes[i];

                    int row = i / cols;        // 행 번호 (0부터)
                    int col = i % cols;        // 열 번호 (0부터)

                    // 그룹 내부 좌표 (상대 좌표 X) -> 절대 좌표로 변환 필요
                    // 그룹의 X + 내부 여백 + (열 * (노드폭 + 간격))
                    node.X = this.X + padding + (col * (maxNodeW + gap));

                    // 그룹의 Y + 헤더 높이 + (행 * (노드높이 + 간격))
                    node.Y = this.Y + headerHeight + (row * (maxNodeH + gap));
                }

                // 3. 그룹 크기 자동 조절 (내용물에 맞춤)
                int totalRows = (int)Math.Ceiling((double)ContainedNodes.Count / cols);

                // 필요한 너비: 패딩*2 + (열개수 * 노드폭) + (간격 * (열개수-1))
                double reqWidth = (padding * 2) + (cols * maxNodeW) + (gap * (cols - 1));
                // 실제 너비는 계산된 값과 현재 값 중 큰 것 (너무 작아지지 않게) 또는 딱 맞게
                this.Width = Math.Max(reqWidth, 150);

                // 필요한 높이: 헤더 + 패딩 + (행개수 * 노드높이) + (간격 * (행개수-1))
                double reqHeight = headerHeight + padding + (totalRows * maxNodeH) + (gap * (totalRows - 1));
                this.Height = Math.Max(reqHeight, 100);

                // ★ 자동 정렬 후에도 크기가 변했으니 이벤트 발생 (Width/Height set에서 발생하겠지만 확실히 하기 위해)
                OnModified?.Invoke(this, EventArgs.Empty);
            }
        }

        // --- Start/Stop All 로직 (변경 없음) ---
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
            // 프로퍼티를 통해 값을 바꾸므로 set 접근자 내의 OnModified가 자동으로 호출됨
            X += dx;
            Y += dy;
            foreach (var node in ContainedNodes)
            {
                node.X += dx;
                node.Y += dy;
            }
        }

        // INetworkService 사용
        public async void AddNode(NodeViewModel node)
        {
            if (!ContainedNodes.Contains(node))
            {
                ContainedNodes.Add(node);
                OnModified?.Invoke(this, EventArgs.Empty); // ★ 내용물 변경됨

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

        // INetworkService 사용
        public async void RemoveNode(NodeViewModel node)
        {
            if (ContainedNodes.Contains(node))
            {
                ContainedNodes.Remove(node);
                OnModified?.Invoke(this, EventArgs.Empty); // ★ 내용물 변경됨

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