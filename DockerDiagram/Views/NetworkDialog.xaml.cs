using System.Windows;
using System.Windows.Controls;

namespace DockerDiagram.Views
{
    public partial class NetworkDialog : Window // 새 도커 네트워크 생성 대화상자
    {
        public string NetworkName => txtName.Text.Trim(); // 네트워크 이름
        public string Driver => (cmbDriver.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "bridge"; // 네트워크 드라이버. 기본값은 "bridge"

        public NetworkDialog()
        {
            InitializeComponent();
            txtName.Focus();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e) // 확인 버튼
        {
            if (string.IsNullOrWhiteSpace(NetworkName)) // 네트워크 이름이 비어있는지 확인
            {
                MessageBox.Show("네트워크 명을 입력하세요.");
                return;
            }
            DialogResult = true;
        }
    }
}