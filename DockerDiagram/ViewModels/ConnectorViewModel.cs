using DockerDiagram.Diagram;
using DockerDiagram.Contracts;
using DockerDiagram.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
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
        private PointCollection _sourceArrowPoints = new PointCollection();
        private int _zIndex = 50;
        private bool _isSelected;
        private bool _isBidirectional;
        private bool _isAttached;
        private string _sourceDataLabel = string.Empty;
        private string _targetDataLabel = string.Empty;

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
        public Point SourcePos => ConnectorRoutePlanner.GetBorderPoint(Source, SourceDir);
        public Point TargetPos => ConnectorRoutePlanner.GetBorderPoint(Target, TargetDir);
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

        /// <summary>
        /// 양방향 연결일 때 시작점에 표시할 화살표 머리입니다.
        /// </summary>
        public PointCollection SourceArrowPoints { get => _sourceArrowPoints; set => SetProperty(ref _sourceArrowPoints, value); }

        public double SourceLabelX => GetEndpointLabelPosition(sourceSide: true).X;
        public double SourceLabelY => GetEndpointLabelPosition(sourceSide: true).Y;
        public double TargetLabelX => GetEndpointLabelPosition(sourceSide: false).X;
        public double TargetLabelY => GetEndpointLabelPosition(sourceSide: false).Y;

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

        public bool IsBidirectional
        {
            get => _isBidirectional;
            set
            {
                if (SetProperty(ref _isBidirectional, value))
                {
                    CalculateArrowHeads();
                    OnPropertyChanged(nameof(SourceLabelX));
                    OnPropertyChanged(nameof(SourceLabelY));
                    OnPropertyChanged(nameof(TargetLabelX));
                    OnPropertyChanged(nameof(TargetLabelY));
                    OnModified?.Invoke(this, EventArgs.Empty);
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
                if (SetProperty(ref _relationType, value))
                {
                    OnPropertyChanged(nameof(StrokeColor));
                    OnModified?.Invoke(this, EventArgs.Empty);
                }
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

        public string SourceDataLabel
        {
            get => _sourceDataLabel;
            set
            {
                if (SetProperty(ref _sourceDataLabel, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(SourceLabelX));
                    OnPropertyChanged(nameof(SourceLabelY));
                    OnModified?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string TargetDataLabel
        {
            get => _targetDataLabel;
            set
            {
                if (SetProperty(ref _targetDataLabel, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(TargetLabelX));
                    OnPropertyChanged(nameof(TargetLabelY));
                    OnModified?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string StrokeColor => RelationType switch
        {
            RelationType.KubernetesVolumeClaim => "#E65100",
            RelationType.VolumeMount or RelationType.NetworkAttach => "#111111",
            RelationType.KubernetesOwner => "#326CE5",
            RelationType.KubernetesSelector => "#0B8043",
            _ => "#111111"
        };
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

            // 연결된 대상이 이동하면 경로를 다시 계산합니다.
            // 이 연결선도 즉각적으로 따라가면서 경로를 다시 계산하도록 '위치 변경 이벤트'에 귀를 열어둡니다.
            Attach();

            CalculateRoute(); // 최초 생성 시 경로 계산
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// 선의 출발지나 도착지가 다른 노드로 아예 변경되었을 때 호출됩니다.
        /// </summary>
        public void UpdateConnection(IConnectableItem newSource, PortDirection newSDir, IConnectableItem newTarget, PortDirection newTDir)
        {
            // 기존 대상의 이벤트 구독을 해제합니다.
            // 이렇게 하지 않으면 옛날 노드가 움직일 때마다 이 선이 불필요하게 다시 계산되는 "좀비(Zombie)" 현상이 발생합니다.
            Detach();

            // 새로운 대상으로 교체
            Source = newSource;
            Target = newTarget;
            SourceDir = newSDir;
            TargetDir = newTDir;

            OnPropertyChanged(nameof(Source));
            OnPropertyChanged(nameof(Target));
            OnPropertyChanged(nameof(SourceDir));
            OnPropertyChanged(nameof(TargetDir));

            // 새로운 대상의 이동 이벤트에 다시 귀를 기울입니다.(+=)
            Attach();

            CalculateRoute(); // 교체된 타겟을 기준으로 경로 재계산
            OnModified?.Invoke(this, EventArgs.Empty);
        }

        public void ReverseDirection()
        {
            if (IsBidirectional)
            {
                (_sourceDataLabel, _targetDataLabel) = (_targetDataLabel, _sourceDataLabel);
                OnPropertyChanged(nameof(SourceDataLabel));
                OnPropertyChanged(nameof(TargetDataLabel));
            }

            UpdateConnection(Target, TargetDir, Source, SourceDir);
        }

        public void RefreshRoute() => CalculateRoute();

        internal void Attach()
        {
            if (_isAttached) return;

            Source.OnPositionChanged += OnNodePositionChanged;
            if (!ReferenceEquals(Source, Target))
                Target.OnPositionChanged += OnNodePositionChanged;
            _isAttached = true;
        }

        internal void Detach()
        {
            if (!_isAttached) return;

            Source.OnPositionChanged -= OnNodePositionChanged;
            if (!ReferenceEquals(Source, Target))
                Target.OnPositionChanged -= OnNodePositionChanged;
            _isAttached = false;
        }
        #endregion

        #region Private Routing Methods
        /// <summary>
        /// OrthogonalRouter를 이용해 두 객체 사이의 가장 짧고 꺾임이 자연스러운 경로를 계산합니다.
        /// </summary>
        private void CalculateRoute()
        {
            if (Source == null || Target == null) return;

            ConnectorRoutePlan route = ConnectorRoutePlanner.Calculate(Source, Target);
            SourceDir = route.SourceDirection;
            TargetDir = route.TargetDirection;
            Points = route.Points;

            OnPropertyChanged(nameof(SourcePos));
            OnPropertyChanged(nameof(TargetPos));
            OnPropertyChanged(nameof(SourceLabelX));
            OnPropertyChanged(nameof(SourceLabelY));
            OnPropertyChanged(nameof(TargetLabelX));
            OnPropertyChanged(nameof(TargetLabelY));
            CalculateArrowHeads();
        }

        private Point GetEndpointLabelPosition(bool sourceSide)
        {
            if (Points == null || Points.Count < 2) return new Point();

            Point endpoint = sourceSide ? Points[0] : Points[^1];
            Point adjacent = sourceSide ? Points[1] : Points[^2];
            Vector inward = adjacent - endpoint;
            if (inward.Length <= 0) return endpoint;
            inward.Normalize();

            Point anchor = endpoint + (inward * 22);
            string label = sourceSide ? SourceDataLabel : TargetDataLabel;
            double estimatedWidth = EstimateLabelWidth(label);
            const double estimatedHeight = 22;

            if (Math.Abs(inward.X) >= Math.Abs(inward.Y))
            {
                double x = inward.X >= 0 ? anchor.X : anchor.X - estimatedWidth;
                double y = IsBidirectional && !sourceSide
                    ? anchor.Y + 4
                    : anchor.Y - estimatedHeight - 4;
                return new Point(x, y);
            }

            double verticalX = IsBidirectional && !sourceSide
                ? anchor.X - estimatedWidth - 8
                : anchor.X + 8;
            double verticalY = inward.Y >= 0 ? anchor.Y : anchor.Y - estimatedHeight;
            return new Point(verticalX, verticalY);
        }

        private static double EstimateLabelWidth(string label)
        {
            if (string.IsNullOrEmpty(label)) return 24;

            double textWidth = label.Sum(character => character > 0xFF ? 10.5 : 6.5);
            return Math.Clamp(textWidth + 10, 24, 150);
        }

        /// <summary>
        /// 선의 가장 끝부분에 그려질 화살표(세모) 모양의 꼭짓점 3개 좌표를 계산합니다.
        /// </summary>
        private void CalculateArrowHeads()
        {
            if (Points == null || Points.Count < 2)
            {
                ArrowPoints = new PointCollection();
                SourceArrowPoints = new PointCollection();
                return;
            }

            ArrowPoints = CreateArrowHead(Points[^1], Points[^2]);
            SourceArrowPoints = IsBidirectional
                ? CreateArrowHead(Points[0], Points[1])
                : new PointCollection();
        }

        private static PointCollection CreateArrowHead(Point tip, Point adjacentPoint)
        {
            Vector direction = tip - adjacentPoint;
            if (direction.Length > 0)
            {
                direction.Normalize();
            }

            double arrowLength = 10;
            double arrowWidth = 4;

            Point basePoint = tip - (direction * arrowLength);
            Vector perpendicular = new Vector(-direction.Y, direction.X);

            return new PointCollection
            {
                tip,
                basePoint + (perpendicular * arrowWidth),
                basePoint - (perpendicular * arrowWidth)
            };
        }
        #endregion

        #region Event Handlers
        private void OnNodePositionChanged(object? sender, EventArgs e)
        {
            if ((Source as ConnectableItemViewModel)?.ParentSheet?.IsLayoutUpdating == true ||
                (Target as ConnectableItemViewModel)?.ParentSheet?.IsLayoutUpdating == true)
            {
                return;
            }
            CalculateRoute();
        }
        #endregion
    }
}
