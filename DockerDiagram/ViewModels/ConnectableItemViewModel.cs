using DockerDiagram.Helpers;
using System;

namespace DockerDiagram.ViewModels
{
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
        private SheetViewModel? _parentSheet;

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

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value))
                    OnSelectionChanged(value);
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

        protected virtual void OnParentSheetChanged(
            SheetViewModel? previous,
            SheetViewModel? current)
        {
        }

        protected virtual void OnBoundsChanged(string propertyName)
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
    }
}
