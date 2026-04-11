using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DockerDiagram.ViewModels;

namespace DockerDiagram
{
    public partial class ContainerDetailWindow : Window
    {
        private DispatcherTimer _timer;

        private readonly ObservableCollection<TextBlock> _logItems = new ObservableCollection<TextBlock>();
        private readonly List<string> _rawLogLines = new List<string>();
        private const int MAX_LOG_LINES = 2000; // 앱이 뻗지 않도록 최대 2000줄만 유지

        public ContainerDetailWindow()
        {
            InitializeComponent();

            // 가상화 ListBox에 스트리밍용 리스트를 연결
            lbLogs.ItemsSource = _logItems;

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;

            this.Loaded += ContainerDetailWindow_Loaded;
            this.Closed += ContainerDetailWindow_Closed;
        }

        private void ContainerDetailWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _timer.Start();

            if (this.DataContext is NodeViewModel vm)
            {
                // ★ 핵심: 통째로 갱신하던 이벤트를 지우고, 백그라운드 스트리밍 파이프를 시작합니다.
                _ = vm.StartLogStreamAsync(OnLogChunkReceived);
            }
        }

        private void ContainerDetailWindow_Closed(object? sender, EventArgs e)
        {
            _timer.Stop();

            if (this.DataContext is NodeViewModel vm)
            {
                // ★ 중요: 창을 닫을 때 도커와의 로그 파이프라인(Stream)을 안전하게 폭파하여 메모리 누수 방지
                vm.StopLogStream();
            }
        }

        private async void Timer_Tick(object? sender, EventArgs e)
        {
            // 사용자가 텍스트 박스에 타이핑 중이거나 슬라이더 조작 중이면 상태 갱신 건너뛰기
            if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is Slider)
            {
                return;
            }

            if (this.DataContext is NodeViewModel nodeVm)
            {
                await nodeVm.RefreshDetailsAsync();
            }
        }

        // ==============================================================================
        // ★ [신규] 백그라운드 스트림에서 로그가 쏟아져 들어올 때 호출되는 콜백 메서드
        // ==============================================================================
        private void OnLogChunkReceived(string logChunk)
        {
            // 백그라운드 스레드에서 UI를 건드리면 크래시가 나므로, UI 스레드(Dispatcher)에 작업을 위임합니다.
            Dispatcher.InvokeAsync(() =>
            {
                string searchTerm = txtLogSearch.Text;
                var lines = logChunk.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                var scrollViewer = GetScrollViewer(lbLogs);
                bool isScrolledToEnd = scrollViewer != null && (scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 5);

                foreach (var line in lines)
                {
                    // 원본 데이터 저장 (나중에 검색할 때 쓰기 위함)
                    _rawLogLines.Add(line);

                    // UI TextBlock 하나 생성 후 바로 리스트에 쏙! (Append)
                    var tb = CreateLogTextBlock(line, searchTerm);
                    _logItems.Add(tb);
                }

                // 메모리 보호: 2000줄이 넘어가면 가장 오래된 옛날 로그부터 지워서 앱을 가볍게 유지
                while (_rawLogLines.Count > MAX_LOG_LINES)
                {
                    _rawLogLines.RemoveAt(0);
                    _logItems.RemoveAt(0);
                }

                // 사용자가 맨 밑을 보고 있었다면, 새 로그가 들어왔을 때 자동으로 스크롤 내려주기
                if (isScrolledToEnd && _logItems.Count > 0)
                {
                    lbLogs.ScrollIntoView(_logItems[_logItems.Count - 1]);
                }
            });
        }

        /// <summary>
        /// 검색어가 바뀔 때마다 전체 로그를 다시 색칠합니다. (스트리밍 구조에 맞게 수정됨)
        /// </summary>
        private void TxtLogSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string searchTerm = txtLogSearch.Text;
            _logItems.Clear();

            // 보관해둔 원본 로그 라인을 꺼내서 검색어에 맞게 다시 형광펜 칠해서 넣기
            foreach (var line in _rawLogLines)
            {
                _logItems.Add(CreateLogTextBlock(line, searchTerm));
            }

            if (_logItems.Count > 0)
            {
                lbLogs.ScrollIntoView(_logItems[_logItems.Count - 1]);
            }
        }

        /// <summary>
        /// 로그 문자열 한 줄을 예쁜 UI(TextBlock)로 만들어 반환합니다.
        /// </summary>
        private TextBlock CreateLogTextBlock(string line, string searchTerm)
        {
            var tb = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12
            };

            bool isError = line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("Fail", StringComparison.OrdinalIgnoreCase);

            bool isWarning = line.Contains("WARN", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(searchTerm) && line.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
            {
                HighlightSearchTermFast(tb, line, searchTerm, isError, isWarning);
            }
            else
            {
                var run = new Run(line);
                ApplyBaseStyle(run, isError, isWarning);
                tb.Inlines.Add(run);
            }

            return tb;
        }

        /// <summary>
        /// TextBlock 내부의 Inlines 속성을 이용하여 검색된 단어만 노란 형광펜을 칠합니다.
        /// </summary>
        private void HighlightSearchTermFast(TextBlock tb, string line, string searchTerm, bool isError, bool isWarning)
        {
            int index = line.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase);

            while (index != -1)
            {
                if (index > 0)
                {
                    var runBefore = new Run(line.Substring(0, index));
                    ApplyBaseStyle(runBefore, isError, isWarning);
                    tb.Inlines.Add(runBefore);
                }

                var runMatch = new Run(line.Substring(index, searchTerm.Length))
                {
                    Background = Brushes.Yellow,
                    Foreground = Brushes.Black,
                    FontWeight = FontWeights.Bold
                };
                tb.Inlines.Add(runMatch);

                line = line.Substring(index + searchTerm.Length);
                index = line.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrEmpty(line))
            {
                var runAfter = new Run(line);
                ApplyBaseStyle(runAfter, isError, isWarning);
                tb.Inlines.Add(runAfter);
            }
        }

        private void ApplyBaseStyle(Run run, bool isError, bool isWarning)
        {
            if (isError)
            {
                run.Foreground = Brushes.Tomato;
                run.FontWeight = FontWeights.Bold;
            }
            else if (isWarning)
            {
                run.Foreground = Brushes.Gold;
            }
            else
            {
                run.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D4D4D4"));
            }
        }

        private ScrollViewer? GetScrollViewer(DependencyObject depObj)
        {
            if (depObj is ScrollViewer scrollViewer) return scrollViewer;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                var child = VisualTreeHelper.GetChild(depObj, i);
                var result = GetScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }
    }
}