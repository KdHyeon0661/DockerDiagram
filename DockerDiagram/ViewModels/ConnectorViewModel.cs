using System;
using System.Windows;
using System.Windows.Media;
using DockerDiagram.Helpers;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    public class ConnectorViewModel : ViewModelBase
    {
        private readonly IDialogService _dialogService;

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

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public Point SourcePos => GetExactBorderPoint(Source, SourceDir);
        public Point TargetPos => GetExactBorderPoint(Target, TargetDir);

        // --- 연결 데이터 ---
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

        // ★ [수정] 도커 서비스 제거. 선은 선일 뿐입니다.
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

            // 노드 위치가 바뀌면 선도 따라 움직임
            Source.OnPositionChanged += OnNodePositionChanged;
            Target.OnPositionChanged += OnNodePositionChanged;

            CalculateRoute();
        }

        // 이벤트 핸들러 및 경로 계산 로직은 그대로 유지
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

            // OrthogonalRouter는 정적 헬퍼이므로 서비스 주입 없이 사용 가능
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
    }
}