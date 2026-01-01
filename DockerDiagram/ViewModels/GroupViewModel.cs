using DockerDiagram.Helpers;
using DockerDiagram.Models;
using DockerDiagram.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace DockerDiagram.ViewModels
{
    public class GroupViewModel : ViewModelBase
    {
        private double _x, _y, _width, _height;
        private string _title = "Group";
        private bool _isSelected;

        // ★ [DI] 서비스 필드 추가
        private readonly IDialogService _dialogService;

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

        // ★ [수정] 생성자에 IDialogService 추가
        public GroupViewModel(double x, double y, double w, double h, IDialogService dialogService, string title = "New Group")
        {
            _dialogService = dialogService; // 저장

            X = x; Y = y; Width = w; Height = h; Title = title;

            ArrangeCommand = new RelayCommand(_ => ArrangeNodes());
            StartAllCommand = new AsyncRelayCommand(StartAllContainers);
            StopAllCommand = new AsyncRelayCommand(StopAllContainers);
        }

        // 기능 1: 정렬
        private void ArrangeNodes()
        {
            if (ContainedNodes.Count == 0) return;

            // (ArrangeDialog는 View이므로 여기서 띄우는 것이 MVVM 위반 소지는 있으나, 
            // 현재 구조상 유지하거나 IDialogService를 확장해야 합니다. 일단 유지합니다.)
            var dlg = new ArrangeDialog();
            dlg.Owner = Application.Current.MainWindow;

            if (dlg.ShowDialog() == true)
            {
                int cols = dlg.Columns;
                double padding = 20;
                double headerHeight = 40;
                double gap = 15;

                double maxNodeW = ContainedNodes.Max(n => n.Width);
                double maxNodeH = ContainedNodes.Max(n => n.Height);

                for (int i = 0; i < ContainedNodes.Count; i++)
                {
                    var node = ContainedNodes[i];

                    int row = i / cols;
                    int col = i % cols;

                    node.X = this.X + padding + (col * (maxNodeW + gap));
                    node.Y = this.Y + headerHeight + (row * (maxNodeH + gap));
                }

                int totalRows = (int)Math.Ceiling((double)ContainedNodes.Count / cols);
                double reqWidth = (padding * 2) + (cols * maxNodeW) + (gap * (cols - 1));
                this.Width = Math.Max(reqWidth, 150);

                double reqHeight = headerHeight + padding + (totalRows * maxNodeH) + (gap * (totalRows - 1));
                this.Height = Math.Max(reqHeight, 100);
            }
        }

        // 기능 2: 모두 실행
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
            // ★ [수정] _dialogService 사용
            _dialogService.ShowMessage($"그룹 내 컨테이너 {executionOrder.Count}개를 순차적으로 실행했습니다.");
        }

        // 기능 3: 모두 정지
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
            // ★ [수정] _dialogService 사용
            _dialogService.ShowMessage($"그룹 내 컨테이너 {executionOrder.Count}개를 순차적으로 정지했습니다.");
        }

        // --- 기본 로직 (변경 없음) ---
        public void MoveBy(double dx, double dy)
        {
            X += dx; Y += dy;
            foreach (var node in ContainedNodes) { node.X += dx; node.Y += dy; }
        }

        public void AddNode(NodeViewModel node) { if (!ContainedNodes.Contains(node)) ContainedNodes.Add(node); }
        public void RemoveNode(NodeViewModel node) { if (ContainedNodes.Contains(node)) ContainedNodes.Remove(node); }

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