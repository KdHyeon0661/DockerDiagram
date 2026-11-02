using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using DockerDiagram.Models;

namespace DockerDiagram.Views
{
    /// <summary>
    /// Prune 결과의 핵심 수치와 선택 가능한 상세 목록을 표시합니다.
    /// </summary>
    public partial class PruneResultDialog : Window
    {
        private readonly DockerPruneResult _result;
        private readonly IReadOnlyList<string> _failures;
        private readonly List<PruneResultItem> _deletedItems;

        public PruneResultDialog(
            DockerPruneResult result,
            string operationName,
            IReadOnlyList<string>? failures = null)
        {
            ArgumentNullException.ThrowIfNull(result);

            InitializeComponent();
            _result = result;
            _failures = failures ?? Array.Empty<string>();
            _deletedItems = BuildDeletedItems(result);

            int deletedCount = _deletedItems.Count;
            ResultTitleText.Text = _failures.Count == 0
                ? "정리 완료"
                : deletedCount == 0
                    ? "정리 실패"
                    : "정리 완료 · 일부 실패";
            OperationNameText.Text = string.IsNullOrWhiteSpace(operationName)
                ? "Docker 리소스 정리"
                : operationName;
            SummaryText.Text = deletedCount == 0
                ? _failures.Count == 0
                    ? "삭제할 리소스가 없었습니다."
                    : "요청한 리소스를 삭제하지 못했습니다."
                : "총 " + FormatCount(deletedCount) + "개 항목을 정리했습니다.";
            DetailHintText.Visibility = deletedCount == 0 ? Visibility.Collapsed : Visibility.Visible;
            ReclaimedSpaceText.Text = DiskUsageFormat.FormatBytes(result.SpaceReclaimed);

            ContainerCountText.Text = FormatCount(result.ContainersDeleted.Count);
            ImageCountText.Text = FormatCount(result.ImagesDeleted.Count);
            VolumeCountText.Text = FormatCount(result.VolumesDeleted.Count);
            NetworkCountText.Text = FormatCount(result.NetworksDeleted.Count);
            BuildCacheCountText.Text = FormatCount(result.BuildCacheDeleted.Count);
            FailureCountText.Text = FormatCount(_failures.Count);

            if (_deletedItems.Count > 0)
            {
                DeletedItemsExpander.Header = "삭제 항목 보기 (" + FormatCount(_deletedItems.Count) + ")";
                DeletedItemsList.ItemsSource = _deletedItems;
                DeletedItemsExpander.Visibility = Visibility.Visible;
            }

            if (_failures.Count > 0)
            {
                SuccessIconPath.Visibility = Visibility.Collapsed;
                FailureIconPath.Visibility = Visibility.Visible;
                ResultIconBorder.Background =
                    (System.Windows.Media.Brush)FindResource("DialogErrorSurfaceBrush");
                ResultIconBorder.BorderBrush =
                    (System.Windows.Media.Brush)FindResource("DialogErrorBorderBrush");
                FailureSummaryText.Text = FormatCount(_failures.Count) + "개 항목을 삭제하지 못했습니다.";
                FailurePanel.Visibility = Visibility.Visible;
                FailedItemsExpander.Header = "실패 상세 보기 (" + FormatCount(_failures.Count) + ")";
                FailedItemsList.ItemsSource = _failures;
                FailedItemsExpander.Visibility = Visibility.Visible;
            }
        }

        private static List<PruneResultItem> BuildDeletedItems(DockerPruneResult result)
        {
            var items = new List<PruneResultItem>();
            items.AddRange(result.ContainersDeleted.Select(value => new PruneResultItem("컨테이너", value)));
            items.AddRange(result.ImagesDeleted
                .Select(image => string.IsNullOrWhiteSpace(image.Deleted) ? image.Untagged : image.Deleted)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => new PruneResultItem("이미지", value)));
            items.AddRange(result.VolumesDeleted.Select(value => new PruneResultItem("볼륨", value)));
            items.AddRange(result.NetworksDeleted.Select(value => new PruneResultItem("네트워크", value)));
            items.AddRange(result.BuildCacheDeleted.Select(value => new PruneResultItem("빌드 캐시", value)));
            return items;
        }

        private string BuildClipboardText()
        {
            var text = new StringBuilder()
                .AppendLine(OperationNameText.Text)
                .AppendLine("컨테이너: " + FormatCount(_result.ContainersDeleted.Count))
                .AppendLine("이미지: " + FormatCount(_result.ImagesDeleted.Count))
                .AppendLine("볼륨: " + FormatCount(_result.VolumesDeleted.Count))
                .AppendLine("네트워크: " + FormatCount(_result.NetworksDeleted.Count))
                .AppendLine("빌드 캐시: " + FormatCount(_result.BuildCacheDeleted.Count))
                .AppendLine("확보한 공간: " + DiskUsageFormat.FormatBytes(_result.SpaceReclaimed));

            if (_deletedItems.Count > 0)
            {
                text.AppendLine().AppendLine("[삭제 항목]");
                foreach (PruneResultItem item in _deletedItems)
                    text.AppendLine("- " + item.Category + ": " + item.Value);
            }

            if (_failures.Count > 0)
            {
                text.AppendLine().AppendLine("[실패]");
                foreach (string failure in _failures)
                    text.AppendLine("- " + failure);
            }

            return text.ToString().TrimEnd();
        }

        private void CopyDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(BuildClipboardText());
                CopyDetailsButton.Content = "복사됨";
            }
            catch (ExternalException)
            {
                CopyDetailsButton.Content = "복사 실패";
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private static string FormatCount(int count) =>
            count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

        private sealed record PruneResultItem(string Category, string Value);
    }
}
