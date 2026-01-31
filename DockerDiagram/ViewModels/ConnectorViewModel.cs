using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using DockerDiagram.Helpers;
using DockerDiagram.Models;
using System.Threading.Tasks;
using System;

namespace DockerDiagram.ViewModels
{
    public class ConnectorViewModel : ViewModelBase
    {
        private readonly IDockerService _dockerService;
        private readonly IDialogService _dialogService;

        public NodeViewModel Source { get; private set; }
        public NodeViewModel Target { get; private set; }
        public PortDirection SourceDir { get; private set; }
        public PortDirection TargetDir { get; private set; }
        public ICommand ApplyIpCommand { get; }

        private PointCollection _points = new PointCollection();
        public PointCollection Points
        {
            get => _points;
            set { _points = value; OnPropertyChanged(); }
        }

        private PointCollection _arrowPoints = new PointCollection();
        public PointCollection ArrowPoints
        {
            get => _arrowPoints;
            set { _arrowPoints = value; OnPropertyChanged(); }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public Point SourcePos => GetExactBorderPoint(Source, SourceDir);
        public Point TargetPos => GetExactBorderPoint(Target, TargetDir);

        public ConnectorViewModel(
            NodeViewModel source,
            NodeViewModel target,
            PortDirection sDir,
            PortDirection tDir,
            IDockerService dockerService,
            IDialogService dialogService)
        {
            _dockerService = dockerService ?? throw new ArgumentNullException(nameof(dockerService));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

            Source = source;
            Target = target;
            SourceDir = sDir;
            TargetDir = tDir;

            Source.OnPositionChanged += OnNodePositionChanged;
            Target.OnPositionChanged += OnNodePositionChanged;

            ApplyIpCommand = new AsyncRelayCommand(ApplyStaticIpAsync);

            CalculateRoute();
        }

        private void OnNodePositionChanged(object? sender, EventArgs e)
        {
            CalculateRoute();
        }

        public void UpdateConnection(NodeViewModel newSource, PortDirection newSDir, NodeViewModel newTarget, PortDirection newTDir)
        {
            if (Source != null) Source.OnPositionChanged -= OnNodePositionChanged;
            if (Target != null) Target.OnPositionChanged -= OnNodePositionChanged;

            // 정보 갱신
            Source = newSource;
            Target = newTarget;
            SourceDir = newSDir;
            TargetDir = newTDir;

            // [FIX] 새 대상에 이벤트 구독
            if (Source != null) Source.OnPositionChanged += OnNodePositionChanged;
            if (Target != null) Target.OnPositionChanged += OnNodePositionChanged;

            CalculateRoute();
        }

        // 경로 계산 로직
        private void CalculateRoute()
        {
            // 위치 변경 알림 (Grip 이동용)
            OnPropertyChanged(nameof(SourcePos));
            OnPropertyChanged(nameof(TargetPos));

            Rect sourceRect = new Rect(Source.X, Source.Y, Source.Width, Source.Height);
            Rect targetRect = new Rect(Target.X, Target.Y, Target.Width, Target.Height);
            Point start = GetExactBorderPoint(Source, SourceDir);
            Point end = GetExactBorderPoint(Target, TargetDir);

            // 라우터 호출
            Points = OrthogonalRouter.GetRoute(start, SourceDir, end, TargetDir, sourceRect, targetRect);
            CalculateArrowHead(end, TargetDir);
        }

        private Point GetExactBorderPoint(NodeViewModel node, PortDirection dir)
        {
            switch (dir)
            {
                case PortDirection.Left: return new Point(node.X, node.CenterY);
                case PortDirection.Right: return new Point(node.X + node.Width, node.CenterY);
                case PortDirection.Top: return new Point(node.CenterX, node.Y);
                case PortDirection.Bottom: return new Point(node.CenterX, node.Y + node.Height);
                default: return new Point(node.CenterX, node.CenterY);
            }
        }

        private void CalculateArrowHead(Point tip, PortDirection destDir)
        {
            Vector dir = new Vector(0, 0);
            switch (destDir)
            {
                case PortDirection.Left: dir = new Vector(-1, 0); break;
                case PortDirection.Right: dir = new Vector(1, 0); break;
                case PortDirection.Top: dir = new Vector(0, -1); break;
                case PortDirection.Bottom: dir = new Vector(0, 1); break;
            }

            Vector normal = new Vector(-dir.Y, dir.X);
            double arrowLength = 10;
            double arrowWidth = 5;
            Point baseP = tip - (dir * arrowLength);

            ArrowPoints = new PointCollection
            {
                tip,
                baseP + (normal * arrowWidth),
                baseP - (normal * arrowWidth)
            };
        }

        // --- 추가 속성들 (RelationType 등) ---
        private RelationType _relationType;
        public RelationType RelationType
        {
            get => _relationType;
            set { _relationType = value; OnPropertyChanged(); }
        }

        private string? _mountPath;
        public string? MountPath
        {
            get => _mountPath;
            set { _mountPath = value; OnPropertyChanged(); }
        }

        private string? _ipAddress;
        public string? IpAddress
        {
            get => _ipAddress;
            set { _ipAddress = value; OnPropertyChanged(); }
        }

        private string _currentAssignedIp = "Auto";
        public string CurrentAssignedIp
        {
            get => _currentAssignedIp;
            set { _currentAssignedIp = value; OnPropertyChanged(); }
        }

        private async Task ApplyStaticIpAsync()
        {
            if (RelationType != RelationType.NetworkAttach) return;

            // 방향이 뒤집혀도 안전하게 판별
            var containerNode = Source.Type == NodeType.Container ? Source :
                                Target.Type == NodeType.Container ? Target : null;

            var networkNode = Source.Type == NodeType.Network ? Source :
                              Target.Type == NodeType.Network ? Target : null;

            if (containerNode == null || networkNode == null) return;
            if (string.IsNullOrEmpty(containerNode.ContainerId)) return; // Docker container id
            if (string.IsNullOrEmpty(networkNode.ContainerId)) return;   // Docker network id

            try
            {
                // [변경] 주입받은 _dockerService 사용
                string networkId = networkNode.ContainerId;
                string containerId = containerNode.ContainerId;

                await _dockerService.DisconnectNetworkAsync(networkId, containerId);
                await _dockerService.ConnectNetworkAsync(
                    networkId,
                    containerId,
                    string.IsNullOrWhiteSpace(IpAddress) ? null : IpAddress
                );

                CurrentAssignedIp = string.IsNullOrWhiteSpace(IpAddress) ? "Auto (Reassigned)" : IpAddress;

                // [변경] _dialogService 사용
                _dialogService.ShowMessage($"네트워크 설정이 적용되었습니다.\nIP: {CurrentAssignedIp}");
            }
            catch (Exception ex)
            {
                // [변경] _dialogService 사용
                _dialogService.ShowMessage($"IP 설정 실패: {ex.Message}");
            }
        }
    }
}