using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Models;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace DockerDiagram.ViewModels
{
    /// <summary>
    /// 컨테이너 리소스 사용량과 차트, 주기적 통계 조회를 관리합니다.
    /// </summary>
    public sealed class ContainerMonitoringViewModel : ViewModelBase, IDisposable
    {
        private readonly IContainerService _containerService;
        private readonly Func<string> _getContainerId;
        private readonly Func<bool> _isRunning;
        private readonly LineSeries _cpuSeries;
        private readonly LineSeries _memorySeries;
        private readonly LineSeries _networkRxSeries;
        private readonly LineSeries _networkTxSeries;
        private readonly LineSeries _blockReadSeries;
        private readonly LineSeries _blockWriteSeries;
        private DispatcherTimer? _statsTimer;
        private bool _disposed;
        private int _refreshInProgress;
        private int _statsFailureReported;
        private int _timeIndex;
        private double _maxCpuCount = 8.0;
        private double _targetCpuCount = 1.0;
        private double _cpuAdjustmentStep = 0.25;
        private long _maxMemoryMb = 8192;

        internal bool IsMonitoring => _statsTimer?.IsEnabled == true;
        private long _targetMemoryMb = 512;
        private long _memoryAdjustmentStepMb = 128;
        private bool _cpuLimitEnabled;
        private bool _memoryLimitEnabled;
        private bool _hasPendingLimitChanges;
        private bool _suppressLimitChangeTracking;
        private string _cpuUsage = "0.0%";
        private double _cpuValue;
        private string _memoryUsage = "0B / 0B";
        private string _memoryUsedText = "0.0MB";
        private double _memoryValue;
        private string _networkIo = "RX 0B/s  ·  TX 0B/s";
        private string _networkIoTotal = "Total RX 0B  ·  TX 0B";
        private string _blockIo = "Read 0B/s  ·  Write 0B/s";
        private string _blockIoTotal = "Total Read 0B  ·  Write 0B";
        private bool _hasStatsError;
        private string _statsStatusText = string.Empty;
        private ContainerStats? _previousStats;
        private DateTimeOffset _previousStatsAt;

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
                MaximumPadding = 0.1,
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

            NetworkIoPlotModel = CreateIoPlotModel();
            _networkRxSeries = CreateLineSeries("RX", "#007ACC");
            _networkTxSeries = CreateLineSeries("TX", "#17A2B8");
            NetworkIoPlotModel.Series.Add(_networkRxSeries);
            NetworkIoPlotModel.Series.Add(_networkTxSeries);

            BlockIoPlotModel = CreateIoPlotModel();
            _blockReadSeries = CreateLineSeries("Read", "#C47A00");
            _blockWriteSeries = CreateLineSeries("Write", "#8E5A00");
            BlockIoPlotModel.Series.Add(_blockReadSeries);
            BlockIoPlotModel.Series.Add(_blockWriteSeries);
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
                {
                    OnPropertyChanged(nameof(CpuSliderValue));
                    MarkLimitDraftChanged();
                }
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
                {
                    OnPropertyChanged(nameof(MemorySliderValue));
                    MarkLimitDraftChanged();
                }
            }
        }

        public bool CpuLimitEnabled
        {
            get => _cpuLimitEnabled;
            set
            {
                if (SetProperty(ref _cpuLimitEnabled, value))
                    MarkLimitDraftChanged();
            }
        }

        public bool MemoryLimitEnabled
        {
            get => _memoryLimitEnabled;
            set
            {
                if (SetProperty(ref _memoryLimitEnabled, value))
                    MarkLimitDraftChanged();
            }
        }

        public bool HasPendingLimitChanges
        {
            get => _hasPendingLimitChanges;
            private set => SetProperty(ref _hasPendingLimitChanges, value);
        }

        public void LoadResourceLimits(
            double maxCpuCount,
            long maxMemoryMb,
            bool cpuLimitEnabled,
            double targetCpuCount,
            bool memoryLimitEnabled,
            long targetMemoryMb)
        {
            MaxCpuCount = maxCpuCount;
            MaxMemoryMb = maxMemoryMb;
            if (HasPendingLimitChanges) return;

            _suppressLimitChangeTracking = true;
            try
            {
                CpuLimitEnabled = cpuLimitEnabled;
                TargetCpuCount = targetCpuCount;
                MemoryLimitEnabled = memoryLimitEnabled;
                TargetMemoryMb = targetMemoryMb;
            }
            finally
            {
                _suppressLimitChangeTracking = false;
            }
        }

        public void AcceptResourceLimitDraft() => HasPendingLimitChanges = false;

        public void DiscardResourceLimitDraft() => HasPendingLimitChanges = false;

        private void MarkLimitDraftChanged()
        {
            if (!_suppressLimitChangeTracking)
                HasPendingLimitChanges = true;
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

        public string MemoryUsedText
        {
            get => _memoryUsedText;
            private set => SetProperty(ref _memoryUsedText, value);
        }

        public double MemoryValue
        {
            get => _memoryValue;
            private set => SetProperty(ref _memoryValue, double.IsNaN(value) ? 0 : value);
        }

        public string NetworkIo
        {
            get => _networkIo;
            private set => SetProperty(ref _networkIo, value);
        }

        public string NetworkIoTotal
        {
            get => _networkIoTotal;
            private set => SetProperty(ref _networkIoTotal, value);
        }

        public string BlockIo
        {
            get => _blockIo;
            private set => SetProperty(ref _blockIo, value);
        }

        public string BlockIoTotal
        {
            get => _blockIoTotal;
            private set => SetProperty(ref _blockIoTotal, value);
        }

        public bool HasStatsError
        {
            get => _hasStatsError;
            private set => SetProperty(ref _hasStatsError, value);
        }

        public string StatsStatusText
        {
            get => _statsStatusText;
            private set => SetProperty(ref _statsStatusText, value);
        }

        public PlotModel CpuPlotModel { get; }
        public PlotModel MemoryPlotModel { get; }
        public PlotModel NetworkIoPlotModel { get; }
        public PlotModel BlockIoPlotModel { get; }

        public void Start()
        {
            if (_disposed || string.IsNullOrWhiteSpace(_getContainerId())) return;

            if (_statsTimer == null)
            {
                _statsTimer = new DispatcherTimer { Interval = RuntimeSyncTiming.ContainerStatsInterval };
                _statsTimer.Tick += StatsTimer_Tick;
            }

            _statsTimer.Start();
        }

        public void Stop() => _statsTimer?.Stop();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_statsTimer != null)
            {
                _statsTimer.Stop();
                _statsTimer.Tick -= StatsTimer_Tick;
                _statsTimer = null;
            }
            GC.SuppressFinalize(this);
        }

        public async Task RefreshAsync(bool appendChartPoint = true)
        {
            if (_disposed ||
                !_isRunning() ||
                Interlocked.CompareExchange(ref _refreshInProgress, 1, 0) != 0)
            {
                return;
            }

            try
            {
                var stats = await _containerService.GetContainerStatsAsync(_getContainerId());
                ApplyStats(stats, appendChartPoint);
                HasStatsError = false;
                StatsStatusText = string.Empty;
                Volatile.Write(ref _statsFailureReported, 0);
            }
            catch (Exception ex)
            {
                HasStatsError = true;
                StatsStatusText = "통계를 가져오지 못했습니다. 마지막 정상 측정값을 표시합니다.";
                if (Interlocked.Exchange(ref _statsFailureReported, 1) == 0)
                    AppLog.Warning("컨테이너 통계 새로 고침 실패", ex);
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
            MemoryUsedText = $"{stats.MemoryUsedMB:F1}MB";
            CpuValue = stats.CpuPercentage;
            MemoryValue = stats.MemoryLimitMB > 0
                ? (stats.MemoryUsedMB / stats.MemoryLimitMB) * 100
                : 0;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            double elapsedSeconds = _previousStats == null
                ? 0
                : Math.Max(0.001, (now - _previousStatsAt).TotalSeconds);
            ulong rxRate = Rate(stats.NetworkRxBytes, _previousStats?.NetworkRxBytes, elapsedSeconds);
            ulong txRate = Rate(stats.NetworkTxBytes, _previousStats?.NetworkTxBytes, elapsedSeconds);
            ulong readRate = Rate(stats.BlockReadBytes, _previousStats?.BlockReadBytes, elapsedSeconds);
            ulong writeRate = Rate(stats.BlockWriteBytes, _previousStats?.BlockWriteBytes, elapsedSeconds);

            NetworkIo = $"RX {FormatIoBytes(rxRate)}/s  ·  TX {FormatIoBytes(txRate)}/s";
            NetworkIoTotal = $"Total RX {FormatIoBytes(stats.NetworkRxBytes)}  ·  TX {FormatIoBytes(stats.NetworkTxBytes)}";
            BlockIo = $"Read {FormatIoBytes(readRate)}/s  ·  Write {FormatIoBytes(writeRate)}/s";
            BlockIoTotal = $"Total Read {FormatIoBytes(stats.BlockReadBytes)}  ·  Write {FormatIoBytes(stats.BlockWriteBytes)}";
            _previousStats = stats;
            _previousStatsAt = now;

            if (!appendChartPoint) return;

            _cpuSeries.Points.Add(new DataPoint(_timeIndex, stats.CpuPercentage));
            _memorySeries.Points.Add(new DataPoint(_timeIndex, MemoryValue));
            _networkRxSeries.Points.Add(new DataPoint(_timeIndex, rxRate));
            _networkTxSeries.Points.Add(new DataPoint(_timeIndex, txRate));
            _blockReadSeries.Points.Add(new DataPoint(_timeIndex, readRate));
            _blockWriteSeries.Points.Add(new DataPoint(_timeIndex, writeRate));
            TrimSeries();
            _timeIndex++;
            UpdateAxisRange();
            InvalidatePlots();
        }

        public void ApplyStoppedState()
        {
            HasStatsError = false;
            StatsStatusText = string.Empty;
            CpuUsage = "0.0%";
            MemoryUsage = "0.0MB / 0.0MB";
            MemoryUsedText = "0.0MB";
            CpuValue = 0;
            MemoryValue = 0;
            NetworkIo = "RX 0B/s  ·  TX 0B/s";
            BlockIo = "Read 0B/s  ·  Write 0B/s";
            _previousStats = null;
            _previousStatsAt = default;

            bool alreadyAtZero = _cpuSeries.Points.Count == 0 ||
                                 (_cpuSeries.Points.Last().Y == 0 &&
                                  _memorySeries.Points.Last().Y == 0 &&
                                  _networkRxSeries.Points.Last().Y == 0 &&
                                  _networkTxSeries.Points.Last().Y == 0 &&
                                  _blockReadSeries.Points.Last().Y == 0 &&
                                  _blockWriteSeries.Points.Last().Y == 0);
            if (alreadyAtZero) return;

            _cpuSeries.Points.Add(new DataPoint(_timeIndex, 0));
            _memorySeries.Points.Add(new DataPoint(_timeIndex, 0));
            _networkRxSeries.Points.Add(new DataPoint(_timeIndex, 0));
            _networkTxSeries.Points.Add(new DataPoint(_timeIndex, 0));
            _blockReadSeries.Points.Add(new DataPoint(_timeIndex, 0));
            _blockWriteSeries.Points.Add(new DataPoint(_timeIndex, 0));
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
                _networkRxSeries.Points.RemoveAt(0);
                _networkTxSeries.Points.RemoveAt(0);
                _blockReadSeries.Points.RemoveAt(0);
                _blockWriteSeries.Points.RemoveAt(0);
            }
        }

        private void UpdateAxisRange()
        {
            var cpuXAxis = CpuPlotModel.Axes.FirstOrDefault(axis => axis.Position == AxisPosition.Bottom);
            var memoryXAxis = MemoryPlotModel.Axes.FirstOrDefault(axis => axis.Position == AxisPosition.Bottom);
            var networkXAxis = NetworkIoPlotModel.Axes.FirstOrDefault(axis => axis.Position == AxisPosition.Bottom);
            var blockXAxis = BlockIoPlotModel.Axes.FirstOrDefault(axis => axis.Position == AxisPosition.Bottom);

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

            if (networkXAxis != null)
            {
                networkXAxis.Minimum = _timeIndex - 60;
                networkXAxis.Maximum = _timeIndex;
            }

            if (blockXAxis != null)
            {
                blockXAxis.Minimum = _timeIndex - 60;
                blockXAxis.Maximum = _timeIndex;
            }
        }

        private void InvalidatePlots()
        {
            CpuPlotModel.InvalidatePlot(true);
            MemoryPlotModel.InvalidatePlot(true);
            NetworkIoPlotModel.InvalidatePlot(true);
            BlockIoPlotModel.InvalidatePlot(true);
        }

        private static PlotModel CreateIoPlotModel()
        {
            var model = new PlotModel { PlotMargins = new OxyThickness(0) };
            model.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Minimum = 0,
                MinimumPadding = 0,
                MaximumPadding = 0.15,
                IsAxisVisible = false,
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColors.LightGray
            });
            model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, IsAxisVisible = false });
            return model;
        }

        private static LineSeries CreateLineSeries(string title, string color) => new()
        {
            Title = title,
            Color = OxyColor.Parse(color),
            StrokeThickness = 2,
            MarkerType = MarkerType.None
        };

        private static ulong Rate(ulong current, ulong? previous, double elapsedSeconds)
        {
            if (!previous.HasValue || elapsedSeconds <= 0 || current < previous.Value)
                return 0;

            return (ulong)((current - previous.Value) / elapsedSeconds);
        }

        private static string FormatIoBytes(ulong bytes) =>
            ByteSizeFormatter.FormatMetric(
                bytes,
                spaceBeforeUnit: false,
                minimumFractionDigits: 1,
                maximumFractionDigits: 1);
    }
}
