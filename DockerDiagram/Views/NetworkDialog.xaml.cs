using DockerDiagram.Helpers; // IDialogService 사용을 위해 추가
using System.Windows;
using System.Windows.Controls;

namespace DockerDiagram.Views
{
    /// <summary>
    /// 다이어그램 캔버스에서 새로운 도커 가상 네트워크(Network) 그룹을 생성하기 위해
    /// 사용자로부터 네트워크 이름과 드라이버(bridge, host 등)를 입력받는 UI 팝업 창(View) 클래스입니다.
    /// </summary>
    public partial class NetworkDialog : Window // 새 도커 네트워크 생성 대화상자
    {
        private readonly IDialogService _dialogService;

        public string NetworkName => txtName.Text.Trim(); // 사용자가 입력한 새로운 네트워크의 이름을 반환합니다.

        public string Driver => (cmbDriver.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "bridge"; // 사용자가 선택한 네트워크 드라이버(예: bridge, macvlan 등)를 반환하며, 기본값은 "bridge"입니다.

        /// <summary>
        /// 대화상자를 초기화하고 다이얼로그 서비스를 주입받습니다.
        /// 창이 열리면 즉시 이름을 입력할 수 있도록 텍스트 박스에 포커스를 맞춥니다.
        /// </summary>
        public NetworkDialog(IDialogService dialogService)
        {
            InitializeComponent();
            _dialogService = dialogService; // 서비스 할당

            txtName.Focus();
        }

        /// <summary>
        /// 'Create' 확인 버튼을 눌렀을 때 실행되며, 네트워크 이름이 정상적으로 입력되었는지 검증한 후 성공 결과를 반환하며 창을 닫습니다.
        /// </summary>
        private void BtnOk_Click(object sender, RoutedEventArgs e) // 확인 버튼
        {
            if (string.IsNullOrWhiteSpace(NetworkName)) // 네트워크 이름이 비어있는지 확인
            {
                _dialogService.ShowError("네트워크 명을 입력하세요.", "입력 오류");
                return;
            }
            DialogResult = true;
        }
    }
}