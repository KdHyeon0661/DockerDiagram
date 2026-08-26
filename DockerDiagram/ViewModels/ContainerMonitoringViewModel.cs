using DockerDiagram.Contracts;
using DockerDiagram.Common;
using DockerDiagram.Models;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DockerDiagram.ViewModels
{
    /// <summary>
    /// 컨테이너 리소스 사용량과 차트, 주기적 통계 조회를 관리합니다.
    /// </summary>
    public sealed class ContainerMonitoringViewModel : ViewModelBase
    {
        private readonly IContainerService _containerService;
        private readonly Func<string> _getContainerId;
        private readonly Func<bool> _isRunning;
        private readonly LineSeries _cpuSeries;
        private readonly LineSeries _memorySeries;
        private DispatcherTimer? _statsTimer;
        private int _refreshInProgress;
        private int _timeIndex;
        private double _maxCpuCount = 8.0;
        private double _targetCpuCount = 1.0;
        private double _cpuAdjustmentStep = 0.25;
        private long _maxMemoryMb = 8192;
        private long _targetMemoryMb = 512;
        private long _memoryAdjustmentStepMb = 128;
        private string _cpuUsage = "0.0%";
        private double _cpuValue;
        private string _memoryUsage = "0B / 0B";
        private double _memoryValue;

        public ContainerMonitoringViewModel(
            IContainerService containerService,
            Func<string> getContainerId,
            Func<bool> isRunning)
        {
            _containerService = containerService;
            _getContainerId = getContainerId;
            _isRunning = isRunning;

            CpuPlotModel = new PlotModel { PlotMargins = new OxyThickness(0) };
            CpuPlotModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Minimum = 0,
                Maximum = 100,
                IsAxisVisible = false,
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColors.LightGray
            });
            CpuPlotModel.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, IsAxisVisible = false });
            _cpuSeries = new LineSeries
            {
                Color = OxyColor.Parse("#28a745"),
                StrokeThickness = 2,
                MarkerType = MarkerType.None
            };
            CpuPlotModel.Series.Add(_cpuSeries);

            MemoryPlotModel = new PlotModel { PlotMargins = new OxyThickness(0) };
            MemoryPlotModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Minimum = 0,
                Maximum = 100,
                IsAxisVisible = false,
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColors.LightGray
            });
            MemoryPlotModel.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, IsAxisVisible = false });
            _memorySeries = new LineSeries
            {
                Color = OxyColor.Parse("#007ACC"),
                StrokeThickness = 2,
                MarkerType = MarkerType.None
            };
            MemoryPlotModel.Series.Add(_memorySeries);
        }

        public double MaxCpuCount
        {
            get => _maxCpuCount;
            set => SetProperty(ref _maxCpuCount, value);
        }

        public double TargetCpuCount
        {
            get => _targetCpuCount;
            set
            {
                if (SetProperty(ref _targetCpuCount, value))
                    OnPropertyChanged(nameof(CpuSliderValue));
            }
        }

        public long MaxMemoryMb
        {
            get => _maxMemoryMb;
            set => SetProperty(ref _maxMemoryMb, value);
        }

        public long TargetMemoryMb
        {
            get => _targetMemoryMb;
            set
            {
                if (SetProperty(ref _targetMemoryMb, value))
                    OnPropertyChanged(nameof(MemorySliderValue));
            }
        }

        public double CpuSliderValue
        {
            get => TargetCpuCount;
            set
            {
                double snapped = Math.Round(
                    value / CpuAdjustmentStep,
                    MidpointRounding.AwayFromZero) * CpuAdjustmentStep;
                TargetCpuCount = Math.Clamp(snapped, 0.1, MaxCpuCount);
            }
        }

        public double MemorySliderValue
        {
            get => TargetMemoryMb;
            set
            {
                long snapped = (long)Math.Round(
                    value / MemoryAdjustmentStepMb,
                    MidpointRounding.AwayFromZero) * MemoryAdjustmentStepMb;
                TargetMemoryMb = Math.Clamp(snapped, 64L, Math.Max(64L, MaxMemoryMb));
            }
        }

        public double CpuAdjustmentStep
        {
            get => _cpuAdjustmentStep;
            private set
            {
                if (!SetProperty(ref _cpuAdjustmentStep, value))
                    return;

                OnPropertyChanged(nameof(IsCpuStepQuarter));
                OnPropertyChanged(nameof(IsCpuStepHalf));
                OnPropertyChanged(nameof(IsCpuStepOne));
            }
        }

        public long MemoryAdjustmentStepMb
        {
            get => _memoryAdjustmentStepMb;
            private set
            {
                if (!SetProperty(ref _memoryAdjustmentStepMb, value))
                    return;

                OnPropertyChanged(nameof(IsMemoryStep64));
                OnPropertyChanged(nameof(IsMemoryStep128));
                OnPropertyChanged(nameof(IsMemoryStep256));
            }
        }

        public bool IsCpuStepQuarter
        {
            get => Math.Abs(CpuAdjustmentStep - 0.25) < 0.001;
            set
            {
                if (value) CpuAdjustmentStep = 0.25;
            }
        }

        public bool IsCpuStepHalf
        {
            get => Math.Abs(CpuAdjustmentStep - 0.5) < 0.001;
            set
            {
                if (value) CpuAdjustmentStep = 0.5;
            }
        }

        public bool IsCpuStepOne
        {
            get => Math.Abs(CpuAdjustmentStep - 1.0) < 0.001;
            set
            {
                if (value) CpuAdjustmentStep = 1.0;
            }
        }

        public bool IsMemoryStep64
        {
            get => MemoryAdjustmentStepMb == 64;
            set
            {
                if (value) MemoryAdjustmentStepMb = 64;
            }
        }

        public bool IsMemoryStep128
        {
            get => MemoryAdjustmentStepMb == 128;
            set
            {
                if (value) MemoryAdjustmentStepMb = 128;
            }
        }

        public bool IsMemoryStep256
        {
            get => MemoryAdjustmentStepMb == 256;
            set
            {
                if (value) MemoryAdjustmentStepMb = 256;
            }
        }

        public string CpuUsage
        {
            get => _cpuUsage;
            private set => SetProperty(ref _cpuUsage, value);
        }

        public double CpuValue
        {
            get => _cpuValue;
            private set => SetProperty(ref _cpuValue, value);
        }

        public string MemoryUsage
        {
            get => _memoryUsage;
            private set => SetProperty(ref _memoryUsage, value);
        }

        public double MemoryValue
        {
            get => _memoryValue;
            private set => SetProperty(ref _memoryValue, double.IsNaN(value) ? 0 : value);
        }

        public PlotModel CpuPlotModel { get; }
        public PlotModel MemoryPlotModel { get; }

        public void Start()
        {
            if (string.IsNullOrWhiteSpace(_getContainerId())) return;

            if (_statsTimer == null)
            {
                _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _statsTimer.Tick += StatsTimer_Tick;
            }

            _statsTimer.Start();
        }

        public void Stop() => _statsTimer?.Stop();

        public async Task RefreshAsync(bool appendChartPoint = true)
        {
            if (!_isRunning() ||
                Interlocked.CompareExchange(ref _refreshInProgress, 1, 0) != 0)
            {
                return;
            }

            try
            {
                var stats = await _containerService.GetContainerStatsAsync(_getContainerId());
                ApplyStats(stats, appendChartPoint);
            }
            catch
            {
            }
            finally
            {
                Volatile.Write(ref _refreshInProgress, 0);
            }
        }

        public void ApplyStats(ContainerStats stats, bool appendChartPoint = true)
        {
            CpuUsage = $"{stats.CpuPercentage:F1}%";
            MemoryUsage = $"{stats.MemoryUsedMB:F1}MB / {stats.MemoryLimitMB:F1}MB";
            CpuValue = stats.CpuPercentage;
            MemoryValue = stats.MemoryLimitMB > 0
                ? (stats.MemoryUsedMB / stats.MemoryLimitMB) * 100
                : 0;

            if (!appendChartPoint) return;

            _cpuSeries.Points.Add(new DataPoint(_timeIndex, stats.CpuPercentage));
            _memorySeries.Points.Add(new DataPoint(_timeIndex, MemoryValue));
            TrimSeries();
            _timeIndex++;
            UpdateAxisRange();
            InvalidatePlots();
        }

        public void ApplyStoppedState()
        {
            CpuUsage = "0.0%";
            MemoryUsage = "0.0MB / 0.0MB";
            CpuValue = 0;
            MemoryValue = 0;

            if (_cpuSeries.Points.Count == 0 || _cpuSeries.Points.Last().Y == 0) return;

            _cpuSeries.Points.Add(new DataPoint(_timeIndex, 0));
            _memorySeries.Points.Add(new DataPoint(_timeIndex, 0));
            TrimSeries();
            _timeIndex++;
            UpdateAxisRange();
            InvalidatePlots();
        }

        private async void StatsTimer_Tick(object? sender, EventArgs e)
        {
            await RefreshAsync();
        }

        private void TrimSeries()
        {
            while (_cpuSeries.Points.Count > 60)
            {
                _cpuSeries.Points.RemoveAt(0);
                _memorySeries.Points.RemoveAt(0);
            }
        }

        private void UpdateAxisRange()
        {
            var cpuXAxis = CpuPlotModel.Axes.FirstOrDefault(axis => axis.Position == AxisPosition.Bottom);
            var memoryXAxis = MemoryPlotModel.Axes.FirstOrDefault(axis => axis.Position == AxisPosition.Bottom);

            if (cpuXAxis != null)
            {
                cpuXAxis.Minimum = _timeIndex - 60;
                cpuXAxis.Maximum = _timeIndex;
            }

            if (memoryXAxis != null)
            {
                memoryXAxis.Minimum = _timeIndex - 60;
                memoryXAxis.Maximum = _timeIndex;
            }
        }

        private void InvalidatePlots()
        {
            CpuPlotModel.InvalidatePlot(true);
            MemoryPlotModel.InvalidatePlot(true);
        }
    }
}
