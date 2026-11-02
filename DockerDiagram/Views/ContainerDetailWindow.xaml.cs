using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DockerDiagram.Contracts;
using DockerDiagram.Common;
using DockerDiagram.ViewModels;

namespace DockerDiagram
{
    public partial class ContainerDetailWindow : Window
    {
        private const int MaxLogLines = 500;

        private readonly List<string> _rawLogLines = [];
        private readonly ObservableCollection<string> _visibleLogLines = [];
        private readonly IDialogService _dialogService;
        private NodeViewModel? _nodeVm;
        private bool _isClosed;
        private readonly DispatcherTimer _detailRefreshTimer;
        private int _detailRefreshInProgress;
        private bool _isLogPaused;
        private bool _isLogStreamActive;

        public ContainerDetailWindow(IDialogService dialogService)
        {
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            InitializeComponent();
            lstLogs.ItemsSource = _visibleLogLines;

            _detailRefreshTimer = new DispatcherTimer { Interval = RuntimeSyncTiming.DetailRefreshInterval };
            _detailRefreshTimer.Tick += DetailRefreshTimer_Tick;


            Loaded += ContainerDetailWindow_Loaded;
            Closed += ContainerDetailWindow_Closed;
        }

        private void ContainerDetailWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _isClosed = false;

            if (DataContext is NodeViewModel vm)
            {
                _nodeVm = vm;
                DetailTabControl.SelectedIndex = vm.IsSwarmService
                    ? 0
                    : vm.IsKubernetesPod
                        ? 1
                        : vm.IsGenericKubernetesResource
                            ? 2
                            : 3;
                _nodeVm.PropertyChanged += NodeVm_PropertyChanged;
                ReplaceLogs(vm.ContainerLogs);
                _isLogStreamActive = true;
                UpdateLogStreamStatus();

                vm.StartMonitoring();
                _detailRefreshTimer.Start();

                // 최근 500줄은 창을 열기 전에 가져왔으므로 이후에 생기는 새 로그만 받습니다.
                _ = ObserveLogStreamAsync(vm);
            }
        }

        private void ContainerDetailWindow_Closed(object? sender, EventArgs e)
        {
            _isClosed = true;
            _isLogStreamActive = false;
            if (txtLogStreamStatus != null)
                txtLogStreamStatus.Text = "연결 종료";
            _detailRefreshTimer.Stop();
            _detailRefreshTimer.Tick -= DetailRefreshTimer_Tick;
            Loaded -= ContainerDetailWindow_Loaded;
            Closed -= ContainerDetailWindow_Closed;

            // OxyPlot의 PlotModel은 한 번에 하나의 PlotView에만 연결될 수 있습니다.
            cpuPlotView.Model = null;
            memoryPlotView.Model = null;
            networkIoPlotView.Model = null;
            blockIoPlotView.Model = null;

            if (_nodeVm != null)
            {
                _nodeVm.PropertyChanged -= NodeVm_PropertyChanged;
                _nodeVm.StopLogStream();
                if (!_nodeVm.IsSelected)
                    _nodeVm.StopMonitoring();
                _nodeVm = null;
            }

            DataContext = null;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void DetailRefreshTimer_Tick(object? sender, EventArgs e)
        {
            if (_isClosed || _nodeVm == null || _nodeVm.IsSelected ||
                Interlocked.Exchange(ref _detailRefreshInProgress, 1) == 1)
            {
                return;
            }

            try
            {
                await _nodeVm.RefreshDetailsAsync(refreshMonitoring: false);
            }
            catch (Exception ex)
            {
                AppLog.WarningThrottled(
                    $"container-detail-refresh-{_nodeVm.ContainerId}",
                    "컨테이너 상세 창 자동 갱신 실패",
                    ex);
            }
            finally
            {
                Interlocked.Exchange(ref _detailRefreshInProgress, 0);
            }
        }

        private async void RefreshDetails_Click(object sender, RoutedEventArgs e)
        {
            if (_nodeVm == null || Interlocked.Exchange(ref _detailRefreshInProgress, 1) == 1)
                return;

            try
            {
                await _nodeVm.RefreshDetailsAsync();
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"상세 정보 갱신 실패: {ex.GetBaseException().Message}", "새로 고침");
            }
            finally
            {
                Interlocked.Exchange(ref _detailRefreshInProgress, 0);
            }
        }

        private void NodeVm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isClosed) return;

            if (e.PropertyName == nameof(NodeViewModel.ContainerLogs) && sender is NodeViewModel vm)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (!_isClosed) ReplaceLogs(vm.ContainerLogs);
                });
            }
        }

        private void OnLogChunkReceived(string logChunk)
        {
            if (_isClosed) return;

            Dispatcher.InvokeAsync(() =>
            {
                if (_isClosed) return;

                string searchTerm = txtLogSearch.Text;
                var lines = logChunk.Split(separator, StringSplitOptions.RemoveEmptyEntries);

                foreach (var line in lines)
                {
                    _rawLogLines.Add(line);
                    if (!_isLogPaused && MatchesSearch(line, searchTerm))
                        _visibleLogLines.Add(line);
                }

                while (_rawLogLines.Count > MaxLogLines)
                {
                    string removedLine = _rawLogLines[0];
                    _rawLogLines.RemoveAt(0);

                    if (!_isLogPaused && MatchesSearch(removedLine, searchTerm) && _visibleLogLines.Count > 0)
                        _visibleLogLines.RemoveAt(0);
                }

                if (!_isLogPaused && chkAutoScroll.IsChecked == true && _visibleLogLines.Count > 0)
                    lstLogs.ScrollIntoView(_visibleLogLines[^1]);
            });
        }

        private async System.Threading.Tasks.Task ObserveLogStreamAsync(NodeViewModel vm)
        {
            try
            {
                await vm.StartLogStreamAsync(OnLogChunkReceived, initialTailCount: 0);
            }
            finally
            {
                _isLogStreamActive = false;
                if (!_isClosed)
                    await Dispatcher.InvokeAsync(UpdateLogStreamStatus);
            }
        }

        private static readonly char[] separator = new[] { '\r', '\n' };

        private void ReplaceLogs(string? logs)
        {
            _rawLogLines.Clear();

            if (!string.IsNullOrWhiteSpace(logs))
            {
                var lines = logs.Split(separator, StringSplitOptions.RemoveEmptyEntries);
                int first = Math.Max(0, lines.Length - MaxLogLines);
                for (int index = first; index < lines.Length; index++)
                    _rawLogLines.Add(lines[index]);
            }

            RenderVisibleLogs(txtLogSearch.Text);
        }

        private void TxtLogSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            RenderVisibleLogs(txtLogSearch.Text);
        }

        private void ConfigurationList_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.C || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                return;
            if (sender is not ListBox listBox || listBox.SelectedItem == null)
                return;

            string? text = listBox.SelectedItem switch
            {
                EnvironmentVariableDisplayItem environment => environment.CopyText,
                PortBindingDisplayItem port => port.CopyText,
                _ => null
            };

            if (string.IsNullOrEmpty(text)) return;
            Clipboard.SetText(text);
            e.Handled = true;
        }


        private void CopyVisibleLogs_Click(object sender, RoutedEventArgs e)
        {
            if (_visibleLogLines.Count == 0) return;

            Clipboard.SetText(string.Join(Environment.NewLine, _visibleLogLines));
        }
        private void RenderVisibleLogs(string searchTerm)
        {
            _visibleLogLines.Clear();
            foreach (var line in _rawLogLines)
            {
                if (MatchesSearch(line, searchTerm))
                    _visibleLogLines.Add(line);
            }

            if (chkAutoScroll.IsChecked == true && _visibleLogLines.Count > 0)
                lstLogs.ScrollIntoView(_visibleLogLines[^1]);
        }

        private void ToggleLogPause_Click(object sender, RoutedEventArgs e)
        {
            _isLogPaused = !_isLogPaused;
            btnLogPause.Content = _isLogPaused ? "재개" : "일시정지";
            UpdateLogStreamStatus();
            if (!_isLogPaused)
                RenderVisibleLogs(txtLogSearch.Text);
        }

        private void UpdateLogStreamStatus()
        {
            txtLogStreamStatus.Text = _isLogPaused
                ? "표시 일시정지"
                : _isLogStreamActive
                    ? "실시간 연결 중"
                    : "스트림 종료";
        }

        private static bool MatchesSearch(string line, string searchTerm) =>
            string.IsNullOrWhiteSpace(searchTerm) ||
            line.Contains(searchTerm, StringComparison.OrdinalIgnoreCase);
    }
}
