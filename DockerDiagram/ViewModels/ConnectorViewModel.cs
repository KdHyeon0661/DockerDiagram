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

        public event EventHandler? OnModified;

        public IConnectableItem Source { get; private set; }
        public IConnectableItem Target { get; private set; }

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
                ZIndex = _isSelected ? 65536 : 50;
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

        // ★ 생성자 인자 타입도 IConnectableItem으로 변경
        public ConnectorViewModel(
            IConnectableItem source,
            IConnectableItem target,
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

        // ★ 업데이트 인자 타입도 IConnectableItem으로 변경
        public void UpdateConnection(IConnectableItem newSource, PortDirection newSDir, IConnectableItem newTarget, PortDirection newTDir)
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

            if (Source == null || Target == null) return;

            Point start = GetExactBorderPoint(Source, SourceDir);
            Point end = GetExactBorderPoint(Target, TargetDir);

            Rect obsSource = Source is GroupViewModel ? new Rect(start.X, start.Y, 0, 0) : new Rect(Source.X, Source.Y, Source.Width, Source.Height);
            Rect obsTarget = Target is GroupViewModel ? new Rect(end.X, end.Y, 0, 0) : new Rect(Target.X, Target.Y, Target.Width, Target.Height);

            try
            {
                // 이제 겹침 검사(꼼수) 없이 당당하게 OrthogonalRouter 알고리즘을 사용합니다!
                var route = OrthogonalRouter.GetRoute(start, SourceDir, end, TargetDir, obsSource, obsTarget);

                if (route == null || route.Count < 2)
                    Points = new PointCollection { start, end };
                else
                    Points = route;
            }
            catch
            {
                Points = new PointCollection { start, end };
            }

            CalculateArrowHead();
        }

        private Point GetExactBorderPoint(IConnectableItem item, PortDirection dir)
        {
            switch (dir)
            {
                case PortDirection.Left: return new Point(item.X, item.CenterY);
                case PortDirection.Right: return new Point(item.X + item.Width, item.CenterY);
                case PortDirection.Top: return new Point(item.CenterX, item.Y);
                case PortDirection.Bottom: return new Point(item.CenterX, item.Y + item.Height);
                default: return new Point(item.CenterX, item.CenterY);
            }
        }

        private void CalculateArrowHead()
        {
            if (Points == null || Points.Count < 2)
            {
                ArrowPoints = new PointCollection();
                return;
            }

            Point endPoint = Points[Points.Count - 1];
            Point prevPoint = Points[Points.Count - 2];

            Vector direction = endPoint - prevPoint;
            if (direction.Length > 0)
            {
                direction.Normalize();
            }

            double arrowLength = 10;
            double arrowWidth = 4;

            Point basePoint = endPoint - (direction * arrowLength);
            Vector perpendicular = new Vector(-direction.Y, direction.X);

            ArrowPoints = new PointCollection
            {
                endPoint,
                basePoint + (perpendicular * arrowWidth),
                basePoint - (perpendicular * arrowWidth)
            };
        }
    }
}