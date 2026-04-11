using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using DockerDiagram.Helpers;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    /// <summary>
    /// 노드(Node)나 그룹(Group) 사이를 잇는 시각적인 연결선(Connector) 상태를 관리하는 뷰모델입니다.
    /// 직각 라우팅 알고리즘을 이용해 최단 거리로 꺾이는 예쁜 선을 계산하고 화면에 그립니다.
    /// </summary>
    public class ConnectorViewModel : ViewModelBase
    {
        #region Fields & Services
        private readonly IDialogService _dialogService;

        private PointCollection _points = new PointCollection();
        private PointCollection _arrowPoints = new PointCollection();
        private int _zIndex = 50;
        private bool _isSelected;

        private RelationType _relationType;
        private string? _mountPath;
        private string? _ipAddress;
        #endregion

        #region Events
        /// <summary>
        /// 데이터가 변경되었음을 부모(시트)에게 알려 도화지 상단에 "수정됨(*)" 표시를 띄우기 위한 이벤트입니다.
        /// </summary>
        public event EventHandler? OnModified;
        #endregion

        #region Connection Properties
        // --- 연결선의 양 끝점 (어떤 객체들을 잇고 있는가) ---
        public IConnectableItem Source { get; private set; }
        public IConnectableItem Target { get; private set; }

        // --- 선이 출발하고 도착하는 방향 (상, 하, 좌, 우) ---
        public PortDirection SourceDir { get; private set; }
        public PortDirection TargetDir { get; private set; }

        // 노드나 그룹의 경계선 상에서 선이 출발/도착할 정확한 X, Y 좌표를 계산하여 반환합니다.
        public Point SourcePos => GetExactBorderPoint(Source, SourceDir);
        public Point TargetPos => GetExactBorderPoint(Target, TargetDir);
        #endregion

        #region Visual Properties
        /// <summary>
        /// 화면에 그려질 선이 꺾이는 지점(좌표)들의 모음입니다.
        /// </summary>
        public PointCollection Points { get => _points; set => SetProperty(ref _points, value); }

        /// <summary>
        /// 선 끝부분에 그려질 화살표 머리(삼각형)의 좌표 모음입니다.
        /// </summary>
        public PointCollection ArrowPoints { get => _arrowPoints; set => SetProperty(ref _arrowPoints, value); }

        // --- 화면 겹침(Z축) 순서 ---
        public int ZIndex { get => _zIndex; set => SetProperty(ref _zIndex, value); }

        /// <summary>
        /// 사용자가 화면에서 이 선을 클릭해서 선택했는지 여부입니다.
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value))
                {
                    // 선이 선택되면 다른 노드나 그룹의 위로 확실하게 돋보이도록 Z-Index를 극단적으로(65536) 끌어올립니다.
                    ZIndex = value ? 65536 : 50;
                }
            }
        }
        #endregion

        #region Data Properties
        // ====================================================================
        // --- 연결 데이터 (선이 품고 있는 추가 속성들) ---
        // 이 값들이 바뀔 때마다 OnModified 이벤트를 발생시켜 저장할 거리가 생겼음을 알립니다.
        // ====================================================================

        public RelationType RelationType
        {
            get => _relationType;
            set
            {
                if (SetProperty(ref _relationType, value)) OnModified?.Invoke(this, EventArgs.Empty);
            }
        }

        public string? MountPath
        {
            get => _mountPath;
            set
            {
                if (SetProperty(ref _mountPath, value)) OnModified?.Invoke(this, EventArgs.Empty);
            }
        }

        public string? IpAddress
        {
            get => _ipAddress;
            set
            {
                if (SetProperty(ref _ipAddress, value)) OnModified?.Invoke(this, EventArgs.Empty);
            }
        }
        #endregion

        #region Constructor
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

            // ★ [핵심] 연결된 대상(노드/그룹)이 마우스 드래그로 움직이면, 
            // 이 연결선도 즉각적으로 따라가면서 경로를 다시 계산하도록 '위치 변경 이벤트'에 귀를 열어둡니다.
            Source.OnPositionChanged += OnNodePositionChanged;
            Target.OnPositionChanged += OnNodePositionChanged;

            CalculateRoute(); // 최초 생성 시 경로 계산
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// 선의 출발지나 도착지가 다른 노드로 아예 변경되었을 때 호출됩니다.
        /// </summary>
        public void UpdateConnection(IConnectableItem newSource, PortDirection newSDir, IConnectableItem newTarget, PortDirection newTDir)
        {
            // ★ [메모리 누수 방지] 기존 대상과의 이벤트 구독(-=)을 반드시 끊어주어야 합니다.
            // 이렇게 하지 않으면 옛날 노드가 움직일 때마다 이 선이 불필요하게 다시 계산되는 "좀비(Zombie)" 현상이 발생합니다.
            if (Source != null) Source.OnPositionChanged -= OnNodePositionChanged;
            if (Target != null) Target.OnPositionChanged -= OnNodePositionChanged;

            // 새로운 대상으로 교체
            Source = newSource;
            Target = newTarget;
            SourceDir = newSDir;
            TargetDir = newTDir;

            // 새로운 대상의 이동 이벤트에 다시 귀를 기울입니다.(+=)
            if (Source != null) Source.OnPositionChanged += OnNodePositionChanged;
            if (Target != null) Target.OnPositionChanged += OnNodePositionChanged;

            CalculateRoute(); // 교체된 타겟을 기준으로 경로 재계산
        }
        #endregion

        #region Private Routing Methods
        /// <summary>
        /// OrthogonalRouter를 이용해 두 객체 사이의 가장 짧고 꺾임이 자연스러운 경로를 계산합니다.
        /// </summary>
        private void CalculateRoute()
        {
            if (Source == null || Target == null) return;

            // 1. 직선거리 기준 상위 4개의 포트 방향 조합을 가져옵니다.
            var candidates = GetTopClosestPorts(4);

            PointCollection? bestRoute = null;
            double bestLength = double.MaxValue;
            PortDirection bestS = PortDirection.Right;
            PortDirection bestT = PortDirection.Left;

            // 2. 상위 4개 후보에 대해 각각 라우팅을 시도해보고, 가장 짧은 최적의 길을 찾습니다.
            foreach (var (sDir, tDir) in candidates)
            {
                Point start = GetExactBorderPoint(Source, sDir);
                Point end = GetExactBorderPoint(Target, tDir);

                // 연결 대상이 그룹일 경우 장애물 박스(Rect)를 0으로 만들어서 선이 그룹 안쪽으로 파고들 수 있게 합니다.
                Rect obsSource = Source is GroupViewModel ? new Rect(start.X, start.Y, 0, 0) : new Rect(Source.X, Source.Y, Source.Width, Source.Height);
                Rect obsTarget = Target is GroupViewModel ? new Rect(end.X, end.Y, 0, 0) : new Rect(Target.X, Target.Y, Target.Width, Target.Height);

                try
                {
                    var route = OrthogonalRouter.GetRoute(start, sDir, end, tDir, obsSource, obsTarget);

                    if (route != null && route.Count >= 2)
                    {
                        double length = GetPathLength(route);

                        // 가장 짧은 거리가 나오면 갱신
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

            // 3. 찾은 최적의 선을 화면에 적용합니다.
            if (bestRoute != null)
            {
                SourceDir = bestS;
                TargetDir = bestT;
                Points = bestRoute;
            }
            else
            {
                // 장애물 등으로 길을 못 찾았을 경우 대비용 최후의 수단 (직선 긋기)
                SourceDir = candidates[0].Item1;
                TargetDir = candidates[0].Item2;
                Points = new PointCollection { GetExactBorderPoint(Source, SourceDir), GetExactBorderPoint(Target, TargetDir) };
            }

            OnPropertyChanged(nameof(SourcePos));
            OnPropertyChanged(nameof(TargetPos));
            CalculateArrowHead();
        }

        /// <summary>
        /// 시작점과 끝점의 4방향(상하좌우) 조합 중, 직선 거리가 가장 짧은 상위 N개를 반환합니다.
        /// </summary>
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
                    // 피타고라스 거리 비교 (루트는 생략하여 속도 최적화)
                    double dist = (sPoint.X - tPoint.X) * (sPoint.X - tPoint.X) +
                                  (sPoint.Y - tPoint.Y) * (sPoint.Y - tPoint.Y);

                    list.Add((sDir, tDir, dist));
                }
            }

            return list.OrderBy(x => x.dist)
                       .Take(topCount)
                       .Select(x => (x.sDir, x.tDir))
                       .ToList();
        }

        /// <summary>
        /// PointCollection에 저장된 경로가 직각으로 꺾일 때, 실제로 총 몇 픽셀을 이동하는지 거리를 잽니다.
        /// </summary>
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

        /// <summary>
        /// 선의 가장 끝부분에 그려질 화살표(세모) 모양의 꼭짓점 3개 좌표를 계산합니다.
        /// </summary>
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
        #endregion

        #region Event Handlers
        private void OnNodePositionChanged(object? sender, EventArgs e)
        {
            CalculateRoute();
        }
        #endregion
    }
}
