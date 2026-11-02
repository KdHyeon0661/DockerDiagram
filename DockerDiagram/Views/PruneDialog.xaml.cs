using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using DockerDiagram.Models;

namespace DockerDiagram.Views
{
    /// <summary>
    /// Docker 환경에서 함께 정리할 리소스 종류와 각 범위를 선택하는 대화상자입니다.
    /// </summary>
    public partial class PruneDialog : Window
    {
        private DockerPruneResource SelectedResources
        {
            get
            {
                DockerPruneResource resources = DockerPruneResource.None;
                if (chkContainers.IsChecked == true)
                    resources |= DockerPruneResource.Containers;
                if (chkImages.IsChecked == true)
                    resources |= DockerPruneResource.Images;
                if (chkVolumesTarget.IsChecked == true)
                    resources |= DockerPruneResource.Volumes;
                if (chkNetworks.IsChecked == true)
                    resources |= DockerPruneResource.Networks;
                if (chkBuildCache.IsChecked == true)
                    resources |= DockerPruneResource.BuildCache;
                return resources;
            }
        }

        public DockerPruneOptions PruneOptions => new()
        {
            Target = DockerPruneTarget.System,
            Resources = SelectedResources,
            IncludeRunningContainers =
                chkContainers.IsChecked == true &&
                chkRunningContainers.IsChecked == true,
            AllImages = chkImages.IsChecked == true && chkAllImages.IsChecked == true,
            IncludeVolumes = chkVolumesTarget.IsChecked == true,
            IncludeAnonymousVolumes =
                chkVolumesTarget.IsChecked == true &&
                chkAnonymousVolumes.IsChecked == true,
            IncludeNamedVolumes =
                chkVolumesTarget.IsChecked == true &&
                chkNamedVolumes.IsChecked == true
        };

        public PruneDialog()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateOptionState();
        }

        private void PruneTarget_Changed(object sender, RoutedEventArgs e)
        {
            if (IsLoaded)
                UpdateOptionState();
        }

        private void PruneOption_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded)
                return;

            // 볼륨 카드가 선택된 동안에는 삭제 범위가 비어 있지 않도록 유지한다.
            if (chkVolumesTarget.IsChecked == true &&
                chkAnonymousVolumes.IsChecked != true &&
                chkNamedVolumes.IsChecked != true)
            {
                if (ReferenceEquals(sender, chkAnonymousVolumes))
                    chkNamedVolumes.IsChecked = true;
                else
                    chkAnonymousVolumes.IsChecked = true;
            }

            UpdateScopeSummary();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            chkContainers.IsChecked = true;
            chkImages.IsChecked = true;
            chkVolumesTarget.IsChecked = true;
            chkNetworks.IsChecked = true;
            chkBuildCache.IsChecked = true;
            UpdateOptionState();
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            chkContainers.IsChecked = false;
            chkImages.IsChecked = false;
            chkVolumesTarget.IsChecked = false;
            chkNetworks.IsChecked = false;
            chkBuildCache.IsChecked = false;
            chkRunningContainers.IsChecked = false;
            UpdateOptionState();
        }

        private void UpdateOptionState()
        {
            bool imagesSelected = chkImages.IsChecked == true;
            bool volumesSelected = chkVolumesTarget.IsChecked == true;
            bool containersSelected = chkContainers.IsChecked == true;

            chkAllImages.IsEnabled = imagesSelected;
            ImageOptionsCard.Opacity = imagesSelected ? 1 : 0.45;
            chkAnonymousVolumes.IsEnabled = volumesSelected;
            chkNamedVolumes.IsEnabled = volumesSelected;
            VolumeOptionsCard.Opacity = volumesSelected ? 1 : 0.45;
            chkRunningContainers.IsEnabled = containersSelected;

            if (!containersSelected)
                chkRunningContainers.IsChecked = false;

            int selectedCount = CountSelectedResources(SelectedResources);
            ExecuteButton.IsEnabled = selectedCount > 0;
            TargetHelpText.Text = selectedCount == 0
                ? "정리할 항목을 하나 이상 선택하세요."
                : $"{selectedCount}개 항목을 함께 정리합니다. 선택된 카드는 파란색으로 표시됩니다.";
            ScopeTitleText.Text = chkRunningContainers.IsChecked == true
                ? "위험 작업 포함"
                : "삭제 예정 범위";
            ExecuteButton.Content = chkRunningContainers.IsChecked == true
                ? "확인 후 정리"
                : "정리 실행";

            UpdateScopeSummary();
        }

        private void UpdateScopeSummary()
        {
            var scopes = new List<string>();
            DockerPruneResource resources = SelectedResources;

            if (resources.HasFlag(DockerPruneResource.Containers))
            {
                scopes.Add(chkRunningContainers.IsChecked == true
                    ? "모든 일반 컨테이너(실행 중 포함)"
                    : "중지된 컨테이너");
            }

            if (resources.HasFlag(DockerPruneResource.Images))
            {
                scopes.Add(chkAllImages.IsChecked == true
                    ? "모든 미사용 이미지"
                    : "dangling 이미지");
            }

            if (resources.HasFlag(DockerPruneResource.Volumes))
            {
                string volumeScope =
                    chkAnonymousVolumes.IsChecked == true && chkNamedVolumes.IsChecked == true
                        ? "미사용 익명·이름 있는 볼륨"
                        : chkAnonymousVolumes.IsChecked == true
                            ? "미사용 익명 볼륨"
                            : "미사용 이름 있는 볼륨";
                scopes.Add(volumeScope);
            }

            if (resources.HasFlag(DockerPruneResource.Networks))
                scopes.Add("미사용 사용자 네트워크");
            if (resources.HasFlag(DockerPruneResource.BuildCache))
                scopes.Add("미사용 빌드 캐시");

            ScopeSummaryText.Text = scopes.Count == 0
                ? "선택된 정리 대상이 없습니다."
                : string.Join(", ", scopes) +
                  "를 정리합니다. 기본 네트워크와 bind mount의 호스트 파일은 삭제하지 않습니다.";

            ExecuteButton.IsEnabled = scopes.Count > 0;
            ScopeTitleText.Text = chkRunningContainers.IsChecked == true
                ? "위험 작업 포함"
                : "삭제 예정 범위";
            ExecuteButton.Content = chkRunningContainers.IsChecked == true
                ? "확인 후 정리"
                : "정리 실행";
        }

        private static int CountSelectedResources(DockerPruneResource resources)
        {
            int count = 0;
            foreach (DockerPruneResource resource in new[]
                     {
                         DockerPruneResource.Containers,
                         DockerPruneResource.Images,
                         DockerPruneResource.Volumes,
                         DockerPruneResource.Networks,
                         DockerPruneResource.BuildCache
                     })
            {
                if (resources.HasFlag(resource))
                    count++;
            }

            return count;
        }

        private void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedResources == DockerPruneResource.None)
                return;

            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
