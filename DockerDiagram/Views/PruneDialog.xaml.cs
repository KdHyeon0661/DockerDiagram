using System.Windows;

namespace DockerDiagram.Views
{
    /// <summary>
    /// 도커 환경의 불필요한 리소스(컨테이너, 이미지, 볼륨, 네트워크 등)를 일괄 정리(Prune)하기 위해
    /// 사용자로부터 청소 대상과 추가 옵션을 선택받는 UI 팝업 창(View) 클래스입니다.
    /// 사용자의 선택을 조합하여 유효한 도커 CLI 명령어를 생성한 뒤 뷰모델로 전달합니다.
    /// </summary>
    public partial class PruneDialog : Window
    {
        /// <summary>
        /// UI에서 선택된 라디오 버튼과 체크박스 옵션들을 조합하여 완성된 실제 도커 명령어 문자열입니다.
        /// 창이 닫힌 후 MainViewModel에서 이 값을 읽어 백그라운드 프로세스로 실행하게 됩니다.
        /// </summary>
        public string FinalCommand { get; private set; } = "";

        /// <summary>
        /// 대청소(Prune) 설정 대화상자를 초기화합니다.
        /// </summary>
        public PruneDialog()
        {
            InitializeComponent();
        }

        /// <summary>
        /// '실행 (Prune)' 버튼 클릭 시 호출되며, 선택된 대상(System, Container 등)과 추가 옵션(-a, --volumes, -f)을 판별하여
        /// 최종적인 도커 명령어를 조립하고 성공(True) 결과를 반환하며 창을 닫습니다.
        /// </summary>
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

        /// <summary>
        /// '취소' 버튼 클릭 시 호출되며, 작업을 취소하고 창을 닫습니다.
        /// </summary>
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}