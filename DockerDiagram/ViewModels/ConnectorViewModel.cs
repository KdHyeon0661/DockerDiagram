using System.Windows;
using System.Windows.Media;
using DockerDiagram.Helpers;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    public class ConnectorViewModel : ViewModelBase
    {
        private readonly IDialogService _dialogService;

        public event EventHandler? OnModified;

        public NodeViewModel Source { get; private set; }
        public NodeViewModel Target { get; private set; }
        public PortDirection SourceDir { get; private set; }
        public PortDirection TargetDir { get; private set; }

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

        // ★ [유지] ZIndex 속성 (기본값 50)
        private int _zIndex = 50;
        public int ZIndex
        {
            get => _zIndex;
            set { _zIndex = value; OnPropertyChanged(); }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                OnPropertyChanged();

                // ★ [유지] 선택 시 최상위(65536)로 이동, 해제 시 복귀
                if (_isSelected)
                {
                    ZIndex = 65536;
                }
                else
                {
                    ZIndex = 50;
                }
            }
        }

        public Point SourcePos => GetExactBorderPoint(Source, SourceDir);
        public Point TargetPos => GetExactBorderPoint(Target, TargetDir);

        // --- 연결 데이터 ---
        private RelationType _relationType;
        public RelationType RelationType
        {
            get => _relationType;
            set
            {
                if (_relationType != value)
                {
                    _relationType = value;
                    OnPropertyChanged();
                    OnModified?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        private string? _mountPath;
        public string? MountPath
        {
            get => _mountPath;
            set
            {
                if (_mountPath != value)
                {
                    _mountPath = value;
                    OnPropertyChanged();
                    OnModified?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        private string? _ipAddress;
        public string? IpAddress
        {
            get => _ipAddress;
            set
            {
                if (_ipAddress != value)
                {
                    _ipAddress = value;
                    OnPropertyChanged();
                    OnModified?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public ConnectorViewModel(
            NodeViewModel source,
            NodeViewModel target,
            PortDirection sDir,
            PortDirection tDir,
            IDialogService dialogService)
        {
            Source = source;
            Target = target;
            SourceDir = sDir;
            TargetDir = tDir;
            _dialogService = dialogService;

            Source.OnPositionChanged += OnNodePositionChanged;
            Target.OnPositionChanged += OnNodePositionChanged;

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

            Source = newSource;
            Target = newTarget;
            SourceDir = newSDir;
            TargetDir = newTDir;

            if (Source != null) Source.OnPositionChanged += OnNodePositionChanged;
            if (Target != null) Target.OnPositionChanged += OnNodePositionChanged;

            CalculateRoute();
        }

        private void CalculateRoute()
        {
            OnPropertyChanged(nameof(SourcePos));
            OnPropertyChanged(nameof(TargetPos));

            Rect sourceRect = new Rect(Source.X, Source.Y, Source.Width, Source.Height);
            Rect targetRect = new Rect(Target.X, Target.Y, Target.Width, Target.Height);
            Point start = GetExactBorderPoint(Source, SourceDir);
            Point end = GetExactBorderPoint(Target, TargetDir);

            Points = OrthogonalRouter.GetRoute(start, SourceDir, end, TargetDir, sourceRect, targetRect);

            // ★ [수정] 인자 없이 호출 (내부적으로 Points를 분석)
            CalculateArrowHead();
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

        private void CalculateArrowHead()
        {
            // 점이 2개 미만이면 방향 계산 불가
            if (Points == null || Points.Count < 2)
            {
                ArrowPoints = new PointCollection();
                return;
            }

            // 선의 마지막 점(Target)과 그 직전 점을 찾음
            Point endPoint = Points[Points.Count - 1];
            Point prevPoint = Points[Points.Count - 2];

            // 진행 방향 벡터 계산 (직전 점 -> 끝점)
            Vector direction = endPoint - prevPoint;
            if (direction.Length > 0)
            {
                direction.Normalize(); // 단위 벡터화
            }

            // 화살표 크기
            double arrowLength = 10;
            double arrowWidth = 4;

            // 1. 화살표 밑동(Base) 위치 (끝점에서 반대로 후퇴)
            Point basePoint = endPoint - (direction * arrowLength);

            // 2. 수직 벡터 (90도 회전) -> 날개 벌리기용
            Vector perpendicular = new Vector(-direction.Y, direction.X);

            // 3. 삼각형 좌표 완성
            ArrowPoints = new PointCollection
            {
                endPoint,                                 // 꼭지점
                basePoint + (perpendicular * arrowWidth), // 날개 1
                basePoint - (perpendicular * arrowWidth)  // 날개 2
            };
        }
    }
}