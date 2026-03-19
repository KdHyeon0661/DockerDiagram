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
            if (Source == null || Target == null) return;

            // 1. 직선거리 기준 상위 4개의 포트 조합을 가져옵니다.
            var candidates = GetTopClosestPorts(4);

            PointCollection? bestRoute = null;
            double bestLength = double.MaxValue;
            PortDirection bestS = PortDirection.Right;
            PortDirection bestT = PortDirection.Left;

            // 2. 상위 4개 후보에 대해 직접 '직각 선(Orthogonal Route)'을 그려보고 길이를 잽니다.
            foreach (var (sDir, tDir) in candidates)
            {
                Point start = GetExactBorderPoint(Source, sDir);
                Point end = GetExactBorderPoint(Target, tDir);

                Rect obsSource = Source is GroupViewModel ? new Rect(start.X, start.Y, 0, 0) : new Rect(Source.X, Source.Y, Source.Width, Source.Height);
                Rect obsTarget = Target is GroupViewModel ? new Rect(end.X, end.Y, 0, 0) : new Rect(Target.X, Target.Y, Target.Width, Target.Height);

                try
                {
                    // 라우터에게 선을 그려달라고 부탁합니다.
                    var route = OrthogonalRouter.GetRoute(start, sDir, end, tDir, obsSource, obsTarget);

                    if (route != null && route.Count >= 2)
                    {
                        // 3. 그려진 선의 실제 길이(비용)를 계산합니다.
                        double length = GetPathLength(route);

                        // 가장 짧은 진짜 최단거리 선을 찾아서 저장!
                        if (length < bestLength)
                        {
                            bestLength = length;
                            bestRoute = route;
                            bestS = sDir;
                            bestT = tDir;
                        }
                    }
                }
                catch { continue; }
            }

            // 4. 최종적으로 가장 짧고 예쁜 선을 화면에 적용합니다.
            if (bestRoute != null)
            {
                SourceDir = bestS;
                TargetDir = bestT;
                Points = bestRoute;
            }
            else
            {
                // (만약의 사태를 대비한 예외 처리) 상위 1순위로 그냥 직선 긋기
                SourceDir = candidates[0].Item1;
                TargetDir = candidates[0].Item2;
                Points = new PointCollection { GetExactBorderPoint(Source, SourceDir), GetExactBorderPoint(Target, TargetDir) };
            }

            OnPropertyChanged(nameof(SourcePos));
            OnPropertyChanged(nameof(TargetPos));
            CalculateArrowHead();
        }

        // =================================================================
        // [헬퍼] 상위 N개의 최단 거리 포트 찾기 (피타고라스 1차 필터링)
        // =================================================================
        private List<(PortDirection, PortDirection)> GetTopClosestPorts(int topCount)
        {
            var dirs = new[] { PortDirection.Top, PortDirection.Bottom, PortDirection.Left, PortDirection.Right };
            var list = new List<(PortDirection sDir, PortDirection tDir, double dist)>();

            foreach (var sDir in dirs)
            {
                Point sPoint = GetExactBorderPoint(Source, sDir);
                foreach (var tDir in dirs)
                {
                    Point tPoint = GetExactBorderPoint(Target, tDir);

                    double dist = (sPoint.X - tPoint.X) * (sPoint.X - tPoint.X) +
                                  (sPoint.Y - tPoint.Y) * (sPoint.Y - tPoint.Y);

                    list.Add((sDir, tDir, dist));
                }
            }

            // 거리가 짧은 순서대로 정렬해서 상위 N개(4개)만 뽑아서 리턴!
            return list.OrderBy(x => x.dist)
                       .Take(topCount)
                       .Select(x => (x.sDir, x.tDir))
                       .ToList();
        }

        // =================================================================
        // [헬퍼] 그려진 선의 실제 길이 구하기 (맨해튼 거리 누적)
        // =================================================================
        private double GetPathLength(PointCollection path)
        {
            double len = 0;
            for (int i = 0; i < path.Count - 1; i++)
            {
                len += Math.Abs(path[i].X - path[i + 1].X) + Math.Abs(path[i].Y - path[i + 1].Y);
            }
            return len;
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