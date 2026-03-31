using DockerDiagram.Helpers; // IDialogService 사용을 위해 추가
using System.Text.RegularExpressions;
using System.Windows;

namespace DockerDiagram.Views
{
    /// <summary>
    /// 다이어그램 캔버스에서 컨테이너와 볼륨을 선(Connector)으로 연결할 때, 
    /// 사용자로부터 컨테이너 내부의 마운트 경로(Target Path)와 권한(Owner) 정보를 입력받는 UI 팝업 창(View) 클래스입니다.
    /// </summary>
    public partial class MountDialog : Window
    {
        private readonly IDialogService _dialogService;

        /// <summary>
        /// 사용자가 입력한 컨테이너 내부의 마운트 대상 절대 경로를 반환합니다.
        /// </summary>
        public string MountPath => txtPath.Text.Trim();

        /// <summary>
        /// (선택 사항) 복원 시 파일 권한 문제를 해결하기 위해 사용자가 입력한 소유자(User:Group) 정보를 반환합니다.
        /// </summary>
        public string VolumeOwner => txtOwner.Text.Trim();

        /// <summary>
        /// 대화상자를 초기화하고 다이얼로그 서비스를 주입받습니다.
        /// 창이 열리면 사용자가 바로 경로를 입력할 수 있도록 텍스트 박스에 포커스를 맞춥니다.
        /// </summary>
        public MountDialog(IDialogService dialogService)
        {
            InitializeComponent();
            _dialogService = dialogService; // 서비스 할당

            txtPath.Focus();
        }

        /// <summary>
        /// 'Connect' 확인 버튼을 눌렀을 때 실행되며, 입력된 마운트 경로가 비어있지 않고 
        /// 올바른 절대 경로(Linux 또는 Windows 스타일) 형식인지 검증합니다.
        /// </summary>
        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(MountPath))
            {
                _dialogService.ShowError("컨테이너 내부 경로(Container Path)는 필수 입력값입니다.", "입력 오류");
                txtPath.Focus();
                return;
            }

            // [수정됨] Windows 컨테이너 호환성 해결
            // 1. 리눅스 스타일 절대 경로: /app/data
            // 2. 윈도우 스타일 절대 경로: C:\app\data (대소문자 무관)
            bool isLinuxStyle = MountPath.StartsWith("/");
            bool isWindowsStyle = Regex.IsMatch(MountPath, @"^[a-zA-Z]:[\\/]");

            if (!isLinuxStyle && !isWindowsStyle)
            {
                _dialogService.ShowError("경로는 절대 경로여야 합니다.\n(예: /app/data 또는 C:\\app\\data)", "경로 오류");
                txtPath.Focus();
                return;
            }

            DialogResult = true;
        }
    }
}