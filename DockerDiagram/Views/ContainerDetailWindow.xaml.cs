using System.Windows;

namespace DockerDiagram
{
    /// <summary>
    /// 다이어그램에서 선택한 컨테이너(또는 노드)의 실시간 리소스 상태, 환경변수, 네트워크, 로그 등을 
    /// 탭(Tab) 형태로 종합하여 보여주는 전용 상세 팝업 창(View) 클래스입니다.
    /// 데이터 바인딩은 NodeViewModel에서 전담하므로, 이 클래스는 창 닫기 등의 순수 UI 상호작용만 담당합니다.
    /// </summary>
    public partial class ContainerDetailWindow : Window
    {
        /// <summary>
        /// 상세 정보 창을 초기화하고 XAML에 정의된 UI 구성요소들을 로드합니다.
        /// </summary>
        public ContainerDetailWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 창 하단의 'Close' 버튼을 눌렀을 때 팝업 창을 닫아 메모리에서 해제합니다.
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}