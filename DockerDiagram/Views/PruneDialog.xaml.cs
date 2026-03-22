using System.Windows;

namespace DockerDiagram.Views
{
    public partial class PruneDialog : Window
    {
        // 조립된 최종 명령어를 밖으로 전달할 프로퍼티
        public string FinalCommand { get; private set; } = "";

        public PruneDialog()
        {
            InitializeComponent();
        }

        private void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            string cmd = "docker";

            // 1. 라디오 버튼 선택에 따른 메인 타겟 설정
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

            // 2. 강제 진행(-f) 옵션 추가 (GUI에서는 보통 넣는 게 좋습니다)
            if (chkForce.IsChecked == true)
            {
                cmd += " -f";
            }

            FinalCommand = cmd;
            DialogResult = true; // 창이 정상적으로 OK 눌려서 닫힘을 알림
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}