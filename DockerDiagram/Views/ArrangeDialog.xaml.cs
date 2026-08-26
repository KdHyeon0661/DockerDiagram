using DockerDiagram.Contracts;
using System.Windows;

namespace DockerDiagram.Views
{
    /// <summary>
    /// 그룹(네트워크 등) 내부에 배치된 여러 개의 노드들을 깔끔한 바둑판 배열로 자동 정렬하기 위해
    /// 사용자로부터 원하는 열(Column)의 개수를 입력받는 UI 팝업 창(View) 클래스입니다.
    /// </summary>
    public partial class ArrangeDialog : Window // 그루핑이 된 컨테이너들의 정렬 대화상자
    {
        private readonly IDialogService _dialogService;

        /// <summary>
        /// 사용자가 확정한 정렬 기준 열(Column)의 개수를 저장하여 뷰모델로 전달합니다.
        /// </summary>
        public int Columns { get; private set; } = 3; // 기본값 3열

        /// <summary>
        /// 대화상자를 초기화하고 다이얼로그 서비스를 주입받습니다.
        /// 창이 열리자마자 바로 숫자를 수정할 수 있도록 텍스트 박스에 포커스를 맞추고 전체 선택합니다.
        /// </summary>
        public ArrangeDialog(IDialogService dialogService)
        {
            InitializeComponent();
            _dialogService = dialogService; // 서비스 할당

            txtCols.Focus();
            txtCols.SelectAll();
        }

        /// <summary>
        /// 'Arrange' 확인 버튼을 눌렀을 때 실행되며, 입력된 값이 1 이상의 올바른 숫자인지 검증한 후 성공 결과를 반환하며 창을 닫습니다.
        /// </summary>
        private void BtnOk_Click(object sender, RoutedEventArgs e) // 확인 버튼
        {
            if (int.TryParse(txtCols.Text, out int c) && c > 0) // 열 개수가 1 이상의 숫자인지 확인
            {
                Columns = c;
                DialogResult = true;
            }
            else
            {
                _dialogService.ShowError("열(Column) 개수는 1 이상의 숫자여야 합니다.", "입력 오류");
            }
        }
    }
}