using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input; // ★ Keyboard.FocusedElement 사용을 위해 추가됨
using System.Windows.Media;
using System.Windows.Threading;
using System.ComponentModel;
using DockerDiagram.ViewModels;

namespace DockerDiagram
{
    public partial class ContainerDetailWindow : Window
    {
        private DispatcherTimer _timer;

        public ContainerDetailWindow()
        {
            InitializeComponent();

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;

            this.Loaded += ContainerDetailWindow_Loaded;
            this.Closed += (s, e) => _timer.Stop();
        }

        private void ContainerDetailWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _timer.Start();

            // ViewModel의 ContainerLogs 속성이 바뀔 때마다 감지해서 ListBox를 갱신하기 위한 이벤트 연결
            if (this.DataContext is INotifyPropertyChanged notifyObj)
            {
                notifyObj.PropertyChanged += ViewModel_PropertyChanged;
            }

            // 처음 창 열릴 때 한번 강제로 그려줌
            UpdateRichTextLogs();
        }

        private async void Timer_Tick(object? sender, EventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is Slider)
            {
                return;
            }

            if (this.DataContext is NodeViewModel nodeVm)
            {
                await nodeVm.RefreshDetailsAsync();
            }
        }

        /// <summary>
        /// ViewModel의 값이 변경되었을 때 호출됩니다.
        /// </summary>
        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // ContainerLogs(로그 원본 데이터)가 새로고침되어 변경될 때마다 화면 갱신
            if (e.PropertyName == "ContainerLogs")
            {
                UpdateRichTextLogs();
            }
        }

        /// <summary>
        /// [고성능 가상화 모드]
        /// ViewModel의 원본 로그(String)를 가벼운 TextBlock 리스트로 변환하여 가상화 ListBox에 바인딩합니다.
        /// 대용량 로그 렌더링 시 메모리와 CPU 점유율을 획기적으로 낮춥니다.
        /// </summary>
        private void UpdateRichTextLogs()
        {
            if (this.DataContext is not NodeViewModel vm) return;

            string rawLogs = vm.ContainerLogs ?? string.Empty;
            string searchTerm = txtLogSearch.Text; // 현재 사용자가 입력한 검색어

            // 줄 단위로 쪼개기
            var lines = rawLogs.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            // UI에 바인딩할 가벼운 텍스트블록 리스트 (최적화를 위해 Capacity를 미리 할당)
            var logItems = new List<TextBlock>(lines.Length);

            foreach (var line in lines)
            {
                // 줄마다 가벼운 TextBlock 하나씩 생성
                var tb = new TextBlock
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12
                };

                bool isError = line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                               line.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
                               line.Contains("Fail", StringComparison.OrdinalIgnoreCase);

                bool isWarning = line.Contains("WARN", StringComparison.OrdinalIgnoreCase);

                // 검색어가 있고, 해당 줄에 검색어가 포함되어 있다면 쪼개서 형광펜 칠하기
                if (!string.IsNullOrWhiteSpace(searchTerm) && line.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                {
                    HighlightSearchTermFast(tb, line, searchTerm, isError, isWarning);
                }
                else
                {
                    // 검색어가 없으면 통째로 추가
                    var run = new Run(line);
                    ApplyBaseStyle(run, isError, isWarning);
                    tb.Inlines.Add(run);
                }

                logItems.Add(tb);
            }

            // 스크롤이 현재 맨 아래를 보고 있는지 판단 (자동 스크롤 유지를 위해)
            var scrollViewer = GetScrollViewer(lbLogs);
            bool isScrolledToEnd = scrollViewer != null && (scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 5);

            // 가상화 ListBox에 데이터를 한 번에 밀어넣기 (UI 스레드 렌더링 부하 최소화)
            lbLogs.ItemsSource = logItems;

            // 맨 아래를 보고 있었다면 업데이트 후에도 맨 아래로 스크롤 유지
            if (isScrolledToEnd && logItems.Count > 0)
            {
                lbLogs.ScrollIntoView(logItems[logItems.Count - 1]);
            }
        }

        /// <summary>
        /// TextBlock 내부의 Inlines 속성을 이용하여 특정 단어만 배경색을 칠합니다.
        /// </summary>
        private void HighlightSearchTermFast(TextBlock tb, string line, string searchTerm, bool isError, bool isWarning)
        {
            int index = line.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase);

            while (index != -1)
            {
                // 검색어 앞부분 추가
                if (index > 0)
                {
                    var runBefore = new Run(line.Substring(0, index));
                    ApplyBaseStyle(runBefore, isError, isWarning);
                    tb.Inlines.Add(runBefore);
                }

                // 검색어 본체 추가 (하이라이트!)
                var runMatch = new Run(line.Substring(index, searchTerm.Length))
                {
                    Background = Brushes.Yellow,  // 형광펜 배경
                    Foreground = Brushes.Black,   // 글씨는 잘 보이게 검은색
                    FontWeight = FontWeights.Bold
                };
                tb.Inlines.Add(runMatch);

                // 검색어 뒷부분 잘라내서 다음 루프로
                line = line.Substring(index + searchTerm.Length);
                index = line.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase);
            }

            // 남은 뒷부분이 있으면 마저 추가
            if (!string.IsNullOrEmpty(line))
            {
                var runAfter = new Run(line);
                ApplyBaseStyle(runAfter, isError, isWarning);
                tb.Inlines.Add(runAfter);
            }
        }

        /// <summary>
        /// 텍스트(Run)에 기본 로그 심각도(Error, Warn, Info)에 따른 색상을 적용합니다.
        /// </summary>
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
                // 일반 텍스트는 밝은 회색
                run.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D4D4D4"));
            }
        }

        /// <summary>
        /// ListBox 내부에 숨겨져 있는 ScrollViewer 컴포넌트를 시각적 트리(VisualTree)에서 찾아냅니다.
        /// </summary>
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

        // 검색창에 글자를 칠 때마다 즉시 다시 그리기
        private void TxtLogSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateRichTextLogs();
        }
    }
}