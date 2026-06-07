using System.Windows;
using DockerDiagram.Models;

namespace DockerDiagram.Views
{
    /// <summary>
    /// 도커 환경의 불필요한 리소스(컨테이너, 이미지, 볼륨, 네트워크 등)를 일괄 정리(Prune)하기 위해
    /// 사용자로부터 청소 대상과 추가 옵션을 선택받는 UI 팝업 창(View) 클래스입니다.
    /// </summary>
    public partial class PruneDialog : Window
    {
        public string FinalCommand { get; private set; } = "";
        public bool IsVolumePruneSelected => rbVolume.IsChecked == true;

        public DockerPruneOptions PruneOptions => new()
        {
            Target = rbContainer.IsChecked == true ? DockerPruneTarget.Container
                : rbImage.IsChecked == true ? DockerPruneTarget.Image
                : rbVolume.IsChecked == true ? DockerPruneTarget.Volume
                : rbNetwork.IsChecked == true ? DockerPruneTarget.Network
                : DockerPruneTarget.System,
            AllImages = chkAllImages.IsChecked == true,
            IncludeVolumes = chkVolumes.IsChecked == true
        };

        public PruneDialog()
        {
            InitializeComponent();
        }

        private void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            string cmd = "docker";

            if (rbSystem.IsChecked == true)
            {
                cmd += " system prune";
                if (chkAllImages.IsChecked == true) cmd += " -a";
                if (chkVolumes.IsChecked == true) cmd += " --volumes";
            }
            else if (rbContainer.IsChecked == true)
            {
                cmd += " container prune";
            }
            else if (rbImage.IsChecked == true)
            {
                cmd += " image prune";
                if (chkAllImages.IsChecked == true) cmd += " -a";
            }
            else if (rbVolume.IsChecked == true)
            {
                cmd += " volume prune";
            }
            else if (rbNetwork.IsChecked == true)
            {
                cmd += " network prune";
            }

            if (chkForce.IsChecked == true)
            {
                cmd += " -f";
            }

            FinalCommand = cmd;
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
