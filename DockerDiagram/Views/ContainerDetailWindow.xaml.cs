using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DockerDiagram.ViewModels;

namespace DockerDiagram
{
    public partial class ContainerDetailWindow : Window
    {
        private const int MaxLogLines = 500;

        private readonly List<string> _rawLogLines = new List<string>();
        private readonly ObservableCollection<string> _visibleLogLines = new ObservableCollection<string>();
        private NodeViewModel? _nodeVm;
        private bool _isClosed;

        public ContainerDetailWindow()
        {
            InitializeComponent();
            lstLogs.ItemsSource = _visibleLogLines;


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

                // 최근 500줄은 창을 열기 전에 가져왔으므로 이후에 생기는 새 로그만 받습니다.
                _ = vm.StartLogStreamAsync(OnLogChunkReceived, initialTailCount: 0);
            }
        }

        private void ContainerDetailWindow_Closed(object? sender, EventArgs e)
        {
            _isClosed = true;
            Loaded -= ContainerDetailWindow_Loaded;
            Closed -= ContainerDetailWindow_Closed;

            // OxyPlot의 PlotModel은 한 번에 하나의 PlotView에만 연결될 수 있습니다.
            cpuPlotView.Model = null;
            memoryPlotView.Model = null;

            if (_nodeVm != null)
            {
                _nodeVm.PropertyChanged -= NodeVm_PropertyChanged;
                _nodeVm.StopLogStream();
                _nodeVm = null;
            }

            DataContext = null;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
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
                var lines = logChunk.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                // Docker가 한 청크 안의 로그를 과거순으로 주므로 앞에 차례로 삽입하면 최신순이 됩니다.
                foreach (var line in lines)
                {
                    _rawLogLines.Insert(0, line);
                    if (MatchesSearch(line, searchTerm))
                        _visibleLogLines.Insert(0, line);
                }

                // 최신 500줄만 유지하고 가장 오래된 항목은 목록의 아래에서 제거합니다.
                while (_rawLogLines.Count > MaxLogLines)
                {
                    string removedLine = _rawLogLines[^1];
                    _rawLogLines.RemoveAt(_rawLogLines.Count - 1);

                    if (MatchesSearch(removedLine, searchTerm) && _visibleLogLines.Count > 0)
                        _visibleLogLines.RemoveAt(_visibleLogLines.Count - 1);
                }
            });
        }

        private void ReplaceLogs(string? logs)
        {
            _rawLogLines.Clear();

            if (!string.IsNullOrWhiteSpace(logs))
            {
                var lines = logs.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int index = lines.Length - 1; index >= 0 && _rawLogLines.Count < MaxLogLines; index--)
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

            if (_visibleLogLines.Count > 0)
                lstLogs.ScrollIntoView(_visibleLogLines[0]);
        }

        private static bool MatchesSearch(string line, string searchTerm) =>
            string.IsNullOrWhiteSpace(searchTerm) ||
            line.Contains(searchTerm, StringComparison.OrdinalIgnoreCase);
    }
}
