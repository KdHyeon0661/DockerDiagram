using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using System.Threading.Tasks;
using DockerDiagram.Helpers;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    public enum RelationType
    {
        Dependency,     // Container <-> Container
        VolumeMount,    // Container <-> Volume
        NetworkAttach   // Container <-> Network
    }

    public class ConnectorViewModel : ViewModelBase
    {
        public NodeViewModel Source { get; private set; }
        public NodeViewModel Target { get; private set; }
        public PortDirection SourceDir { get; private set; }
        public PortDirection TargetDir { get; private set; }
        public ICommand ApplyIpCommand { get; }

        private PointCollection _points;
        public PointCollection Points
        {
            get => _points;
            set { _points = value; OnPropertyChanged(); }
        }

        private PointCollection _arrowPoints;
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

        // --- ★ [NEW] 관계 및 추가 정보 속성 ---

        private RelationType _relationType;
        public RelationType RelationType
        {
            get => _relationType;
            set { _relationType = value; OnPropertyChanged(); }
        }

        public string CurrentAssignedIp
        {
            get
            {
                // Source가 컨테이너이고, Target이 네트워크일 때
                if (Source.Type == NodeType.Container && Target.Type == NodeType.Network)
                {
                    // 컨테이너가 해당 네트워크의 IP를 가지고 있는지 확인
                    if (Source.NetworkIpMap.ContainsKey(Target.Name))
                    {
                        return Source.NetworkIpMap[Target.Name];
                    }
                    // 아직 실행 전이거나 연결되지 않음
                    return "Not Assigned (Running?)";
                }
                return "-";
            }
        }

        // 볼륨 마운트 경로 (예: /var/lib/mysql)
        private string _mountPath = "";
        public string MountPath
        {
            get => _mountPath;
            set { _mountPath = value; OnPropertyChanged(); }
        }

        // 네트워크 고정 IP (예: 172.18.0.5)
        private string _ipAddress = "";
        public string IpAddress
        {
            get => _ipAddress;
            set { _ipAddress = value; OnPropertyChanged(); }
        }

        // 생성자
        public ConnectorViewModel(NodeViewModel source, NodeViewModel target, PortDirection sourceDir, PortDirection targetDir)
        {
            Source = source;
            Target = target;
            SourceDir = sourceDir;
            TargetDir = targetDir;

            _points = new PointCollection();
            _arrowPoints = new PointCollection();

            DetermineRelationType();

            Source.OnPositionChanged += (s, e) => CalculateRoute();
            Target.OnPositionChanged += (s, e) => CalculateRoute();

            // 컨테이너(Source)의 속성이 변하면(RefreshDetailsAsync 호출 시),커넥터의 CurrentAssignedIp도 화면에서 갱신되도록 알림
            Source.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(NodeViewModel.NetworkIpMap) ||
                    e.PropertyName == "IsRunning") // 실행 상태가 변해도 갱신
                {
                    OnPropertyChanged(nameof(CurrentAssignedIp));
                }
            };

            CalculateRoute();
            ApplyIpCommand = new RelayCommand(async _ => await ApplyStaticIpAsync());
        }

        private async Task ApplyStaticIpAsync()
        {
            if (string.IsNullOrWhiteSpace(IpAddress))
            {
                MessageBox.Show("IP 주소를 입력해주세요.");
                return;
            }

            // 안전장치: 네트워크 연결 타입이 아니면 무시
            if (RelationType != RelationType.NetworkAttach) return;

            var api = new DockerApiService();
            try
            {
                // 1. 기존 연결 끊기 (Disconnect)
                await api.DisconnectNetworkAsync(Target.ContainerId, Source.ContainerId);

                // 2. 고정 IP로 다시 연결 (Connect)
                await api.ConnectNetworkAsync(Target.ContainerId, Source.ContainerId, IpAddress);

                // 3. UI 갱신 (Source 컨테이너 정보를 새로고침하면 Assigned IP도 바뀜)
                await Source.RefreshDetailsAsync();

                MessageBox.Show($"IP({IpAddress})가 성공적으로 적용되었습니다.", "성공", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"IP 적용 실패: {ex.Message}\n(이미 사용 중인 IP이거나 네트워크 대역이 맞지 않을 수 있습니다.)", "오류", MessageBoxButton.OK, MessageBoxImage.Error);

                // 실패 시 복구 시도 (IP 없이 재연결)
                try { await api.ConnectNetworkAsync(Target.ContainerId, Source.ContainerId); } catch { }
            }
        }

        private void DetermineRelationType()
        {
            // MainViewModel에서 검증(Validation)을 거쳐서 들어오므로, 
            // 여기서는 타입에 따라 Enum만 설정하면 됩니다.
            // (Source는 항상 Container라고 가정 - MainViewModel에서 정렬해줌)

            if (Source.Type == NodeType.Container && Target.Type == NodeType.Container)
            {
                RelationType = RelationType.Dependency;
            }
            else if (Target.Type == NodeType.Volume)
            {
                RelationType = RelationType.VolumeMount;
            }
            else if (Target.Type == NodeType.Network)
            {
                RelationType = RelationType.NetworkAttach;
            }
        }

        public void UpdateConnection(NodeViewModel newSource, PortDirection newSourceDir, NodeViewModel newTarget, PortDirection newTargetDir)
        {
            Source.OnPositionChanged -= (s, e) => CalculateRoute();
            Target.OnPositionChanged -= (s, e) => CalculateRoute();

            Source = newSource;
            SourceDir = newSourceDir;
            Target = newTarget;
            TargetDir = newTargetDir;

            DetermineRelationType();

            Source.OnPositionChanged += (s, e) => CalculateRoute();
            Target.OnPositionChanged += (s, e) => CalculateRoute();

            CalculateRoute();
        }

        public void CalculateRoute()
        {
            Point start = GetExactBorderPoint(Source, SourceDir);
            Point end = GetExactBorderPoint(Target, TargetDir);

            OnPropertyChanged(nameof(SourcePos));
            OnPropertyChanged(nameof(TargetPos));

            Rect sourceRect = new Rect(Source.X, Source.Y, Source.Width, Source.Height);
            Rect targetRect = new Rect(Target.X, Target.Y, Target.Width, Target.Height);

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

            ArrowPoints = new PointCollection { tip, baseP + (normal * arrowWidth), baseP - (normal * arrowWidth) };
        }
    }
}