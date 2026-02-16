using System.Windows;

namespace DockerDiagram.Views
{
    public partial class VolumeDialog : Window // 새 도커 볼륨 생성 대화상자
    {
        public string VolumeName => txtName.Text.Trim(); // 볼륨 이름
        public string Driver => txtDriver.Text.Trim(); // 드라이버. 기본값은 "local"



        public VolumeDialog()
        {
            InitializeComponent();
            txtName.Focus();

        }

        // 확인 버튼
        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(VolumeName)) // 볼륨 이름이 비어있는지 확인
            {
                MessageBox.Show("볼륨 명을 입력하세요.");
                return;
            }
            DialogResult = true;
        }
    }
}