using System;
using DockerDiagram.Common;
using DockerDiagram.Models;

namespace DockerDiagram.ViewModels
{
    /// <summary>
    /// 다이어그램 도화지 위에서 선(Connector)으로 연결될 수 있는 모든 시각적 요소(노드, 그룹 등)가 공통으로 가져야 할 규격을 정의합니다.
    /// 연결선은 대상의 구체적인 타입(컨테이너, 볼륨 등)을 몰라도 이 인터페이스의 좌표 정보만 보고 선을 정확히 그릴 수 있습니다.
    /// </summary>
    public interface IConnectableItem
    {
        string Id { get; }      // 요소의 고유 식별자 (선이 대상을 기억할 때 사용)
        string Name { get; }    // UI나 로그에 표시될 요소의 이름

        double X { get; }       // 도화지 상의 좌측 상단 X 좌표
        double Y { get; }       // 도화지 상의 좌측 상단 Y 좌표
        double Width { get; }   // 요소의 너비
        double Height { get; }  // 요소의 높이

        double CenterX { get; } // 요소의 정중앙 X 좌표 (주로 선이 연결되는 기준점)
        double CenterY { get; } // 요소의 정중앙 Y 좌표 (주로 선이 연결되는 기준점)
        bool UsePointRouting { get; } // 그룹처럼 경계 상자 대신 연결점을 기준으로 라우팅할지 여부

        // UI에서 마우스 드래그로 요소의 위치나 크기가 변할 때, 자신에게 연결된 선들에게 "나 움직였으니 선 위치 다시 계산해!" 라고 알려주는 이벤트
        event EventHandler? OnPositionChanged;
    }

    /// <summary>
    /// Common state for canvas elements that can move, resize, select, and connect.
    /// </summary>
    public abstract class ConnectableItemViewModel : ViewModelBase, IConnectableItem
    {
        private string _id = Guid.NewGuid().ToString();
        private double _x;
        private double _y;
        private double _width;
        private double _height;
        private bool _isSelected;
        private bool _isPrimarySelection;
        private SheetViewModel? _parentSheet;
        private RuntimeKind _runtimeKind = RuntimeKind.DockerEngine;
        private RuntimeResourceKind _resourceKind = RuntimeResourceKind.Unspecified;
        private RuntimeBindingState _bindingState = RuntimeBindingState.Bound;

        protected ConnectableItemViewModel(double x, double y, double width, double height)
        {
            _x = x;
            _y = y;
            _width = width;
            _height = height;
        }

        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        public abstract string Name { get; set; }

        public RuntimeKind RuntimeKind
        {
            get => _runtimeKind;
            set => SetRuntimeMetadata(ref _runtimeKind, value, nameof(RuntimeKind));
        }

        public RuntimeResourceKind ResourceKind
        {
            get => _resourceKind;
            set => SetRuntimeMetadata(ref _resourceKind, value, nameof(ResourceKind));
        }

        public RuntimeBindingState BindingState
        {
            get => _bindingState;
            set => SetRuntimeMetadata(ref _bindingState, value, nameof(BindingState));
        }

        public bool IsDraft => BindingState == RuntimeBindingState.Draft;
        public bool HasRuntimeMetadata => ResourceKind != RuntimeResourceKind.Unspecified;
        public bool ShowRuntimeBadge =>
            HasRuntimeMetadata &&
            ResourceKind != RuntimeResourceKind.SwarmVisualGroup;
        public string RuntimeBadgeText => HasRuntimeMetadata
            ? $"{GetResourceKindLabel(ResourceKind)} · {BindingState}"
            : string.Empty;

        public double X
        {
            get => _x;
            set => SetBoundsValue(ref _x, NormalizeX(value), nameof(X), nameof(CenterX));
        }

        public double Y
        {
            get => _y;
            set => SetBoundsValue(ref _y, NormalizeY(value), nameof(Y), nameof(CenterY));
        }

        public double Width
        {
            get => _width;
            set => SetBoundsValue(ref _width, NormalizeWidth(value), nameof(Width), nameof(CenterX));
        }

        public double Height
        {
            get => _height;
            set => SetBoundsValue(ref _height, NormalizeHeight(value), nameof(Height), nameof(CenterY));
        }

        public double CenterX => X + Width / 2;
        public double CenterY => Y + Height / 2;

        /// <summary>
        /// X와 Y를 하나의 위치 변경으로 적용합니다. 드래그처럼 두 좌표가 함께 바뀌는
        /// 작업에서 위치 변경 구독자가 중간 좌표를 기준으로 두 번 처리하지 않게 합니다.
        /// </summary>
        public bool SetPosition(double x, double y)
        {
            double normalizedX = NormalizeX(x);
            double normalizedY = NormalizeY(y);
            bool xChanged = SetProperty(ref _x, normalizedX, nameof(X));
            bool yChanged = SetProperty(ref _y, normalizedY, nameof(Y));

            if (!xChanged && !yChanged)
                return false;

            if (xChanged)
                OnPropertyChanged(nameof(CenterX));

            if (yChanged)
                OnPropertyChanged(nameof(CenterY));

            RaisePositionChanged();
            RaiseModified();

            if (xChanged)
                OnBoundsChanged(nameof(X));

            if (yChanged)
                OnBoundsChanged(nameof(Y));

            return true;
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value))
                    OnSelectionChanged(value);
            }
        }

        /// <summary>
        /// 단일 상세 사이드바가 이 항목을 표시하는지 나타냅니다.
        /// 다중 선택 중에는 IsSelected만 true이고 이 값은 모든 항목에서 false입니다.
        /// </summary>
        public bool IsPrimarySelection
        {
            get => _isPrimarySelection;
            set
            {
                if (SetProperty(ref _isPrimarySelection, value))
                    OnPrimarySelectionChanged(value);
            }
        }

        public virtual SheetViewModel? ParentSheet
        {
            get => _parentSheet;
            set
            {
                if (ReferenceEquals(_parentSheet, value)) return;

                var previous = _parentSheet;
                _parentSheet = value;
                OnPropertyChanged();
                OnParentSheetChanged(previous, value);
            }
        }

        public virtual bool UsePointRouting => false;

        public event EventHandler? OnPositionChanged;
        public event EventHandler? OnModified;

        protected virtual double NormalizeX(double value) => value;
        protected virtual double NormalizeY(double value) => value;
        protected virtual double NormalizeWidth(double value) => value;
        protected virtual double NormalizeHeight(double value) => value;
        protected virtual void OnSelectionChanged(bool isSelected)
        {
        }

        protected virtual void OnPrimarySelectionChanged(bool isPrimarySelection)
        {
        }

        protected virtual void OnParentSheetChanged(
            SheetViewModel? previous,
            SheetViewModel? current)
        {
        }

        protected virtual void OnBoundsChanged(string propertyName)
        {
        }

        protected virtual void OnRuntimeMetadataChanged(string propertyName)
        {
        }

        protected void RaiseModified() => OnModified?.Invoke(this, EventArgs.Empty);
        protected void RaisePositionChanged() => OnPositionChanged?.Invoke(this, EventArgs.Empty);

        private void SetBoundsValue(
            ref double field,
            double value,
            string propertyName,
            string centerPropertyName)
        {
            if (!SetProperty(ref field, value, propertyName)) return;

            OnPropertyChanged(centerPropertyName);
            RaisePositionChanged();
            RaiseModified();
            OnBoundsChanged(propertyName);
        }

        private void SetRuntimeMetadata<T>(ref T field, T value, string propertyName)
        {
            if (!SetProperty(ref field, value, propertyName)) return;

            OnPropertyChanged(nameof(IsDraft));
            OnPropertyChanged(nameof(HasRuntimeMetadata));
            OnPropertyChanged(nameof(ShowRuntimeBadge));
            OnPropertyChanged(nameof(RuntimeBadgeText));
            RaiseModified();
            OnRuntimeMetadataChanged(propertyName);
        }

        private static string GetResourceKindLabel(RuntimeResourceKind kind) => kind switch
        {
            RuntimeResourceKind.SwarmService => "Swarm Service",
            RuntimeResourceKind.SwarmVolume => "Swarm Volume",
            RuntimeResourceKind.SwarmExternalTraffic => "External Traffic",
            RuntimeResourceKind.SwarmOverlayNetwork => "Overlay Network",
            RuntimeResourceKind.SwarmSecret => "Swarm Secret",
            RuntimeResourceKind.SwarmConfig => "Swarm Config",
            RuntimeResourceKind.SwarmVisualGroup => "Swarm Group",
            RuntimeResourceKind.DockerContainer => "Container",
            RuntimeResourceKind.DockerVolume => "Volume",
            RuntimeResourceKind.DockerNetwork => "Network",
            RuntimeResourceKind.ExternalTraffic => "External Traffic",
            RuntimeResourceKind.VisualGroup => "Group",
            _ => kind.ToString()
        };

    }
}
