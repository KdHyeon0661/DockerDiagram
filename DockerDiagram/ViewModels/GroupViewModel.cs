using DockerDiagram.Helpers;
using DockerDiagram.Models;
using DockerDiagram.Views;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace DockerDiagram.ViewModels
{
    public class GroupViewModel : ViewModelBase
    {
        private double _x, _y, _width, _height;
        private string _title = "Group";
        private bool _isSelected;

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

        // Commands
        public ICommand ArrangeCommand { get; }
        public ICommand StartAllCommand { get; }
        public ICommand StopAllCommand { get; }

        public SheetViewModel? ParentSheet { get; set; }

        public GroupViewModel(double x, double y, double w, double h, string title = "New Group")
        {
            X = x; Y = y; Width = w; Height = h; Title = title;

            ArrangeCommand = new RelayCommand(_ => ArrangeNodes());
            StartAllCommand = new AsyncRelayCommand(StartAllContainers);
            StopAllCommand = new AsyncRelayCommand(StopAllContainers);
        }

        // 기능 1: 정렬
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

                    int row = i / cols;       // 행 번호 (0부터)
                    int col = i % cols;       // 열 번호 (0부터)

                    // 그룹 내부 좌표 (상대 좌표 X) -> 절대 좌표로 변환 필요
                    // 그룹의 X + 내부 여백 + (열 * (노드폭 + 간격))
                    node.X = this.X + padding + (col * (maxNodeW + gap));

                    // 그룹의 Y + 헤더 높이 + (행 * (노드높이 + 간격))
                    node.Y = this.Y + headerHeight + (row * (maxNodeH + gap));

                    // 노드 크기 통일. 고민해볼 문제
                    // node.Width = maxNodeW;
                    // node.Height = maxNodeH;
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
            }
        }

        // 기능 2: 모두 실행
        private async Task StartAllContainers()
        {
            var executionOrder = GetExecutionOrder(); // 순서 계산 로직 분리
            if (executionOrder == null || executionOrder.Count == 0) return;

            foreach (var node in executionOrder)
            {
                if (node.IsRunning) continue;

                if (node.StartCommand.CanExecute(null))
                {
                    node.StartCommand.Execute(null);
                    // DB가 켜지고 초기화될 시간을 벌어주기 위해 딜레이
                    await Task.Delay(1500);
                }
            }
            MessageBox.Show($"그룹 내 컨테이너 {executionOrder.Count}개를 순차적으로 실행했습니다.");
        }

        // 기능 3: 모두 정지
        private async Task StopAllContainers()
        {
            var executionOrder = GetExecutionOrder();
            if (executionOrder == null || executionOrder.Count == 0) return;

            // ★ 실행의 역순으로 정지 (Web 끄고 -> DB 끄기)
            executionOrder.Reverse();

            foreach (var node in executionOrder)
            {
                if (!node.IsRunning) continue;

                if (node.StopCommand.CanExecute(null))
                {
                    node.StopCommand.Execute(null);
                    await Task.Delay(500); // 약간의 텀
                }
            }
            MessageBox.Show($"그룹 내 컨테이너 {executionOrder.Count}개를 순차적으로 정지했습니다.");
        }

        // --- 기본 로직 ---
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

        public void AddNode(NodeViewModel node)
        {
            if (!ContainedNodes.Contains(node)) ContainedNodes.Add(node);
        }

        public void RemoveNode(NodeViewModel node)
        {
            if (ContainedNodes.Contains(node)) ContainedNodes.Remove(node);
        }

        private List<NodeViewModel>? GetExecutionOrder()
        {
            if (ParentSheet == null) return null;

            // 1. 그룹 내 컨테이너만 필터링
            var containers = ContainedNodes.Where(n => n.Type == NodeType.Container).ToList();
            if (containers.Count == 0) return null;

            // 2. 그래프 초기화
            // Key: 먼저 실행돼야 할 놈, Value: 그 다음에 실행될 놈들
            var dependencies = new Dictionary<NodeViewModel, List<NodeViewModel>>();
            var inDegree = new Dictionary<NodeViewModel, int>();

            foreach (var c in containers)
            {
                dependencies[c] = new List<NodeViewModel>();
                inDegree[c] = 0;
            }

            // 3. 연결선 분석 (우선순위 결정)
            foreach (var conn in ParentSheet.Connectors)
            {
                if (containers.Contains(conn.Source) && containers.Contains(conn.Target))
                {
                    // Source(Web) -> Target(DB) 연결이라면
                    // DB가 먼저 켜져야 함. 즉, DB -> Web 순서로 실행되어야 함.
                    // 따라서 그래프 상에서는 Target이 부모, Source가 자식.

                    dependencies[conn.Target].Add(conn.Source);
                    inDegree[conn.Source]++;
                }
            }

            // 4. 위상 정렬 (진입 차수가 0인 = 의존성 없는 놈부터 큐에 넣음)
            var queue = new Queue<NodeViewModel>();
            foreach (var c in containers)
            {
                if (inDegree[c] == 0) queue.Enqueue(c);
            }

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

            // 사이클 등으로 인해 빠진 노드가 있다면 뒤에 추가
            foreach (var c in containers)
            {
                if (!order.Contains(c)) order.Add(c);
            }

            return order;
        }
    }
}