using System.Windows;

namespace DockerDiagram.Views
{
    /// <summary>
    /// 로컬 도커 엔진에 다운로드된 이미지(Image) 목록을 조회하고 관리(검색, 삭제)할 수 있는 전용 UI 팝업 창(View) 클래스입니다.
    /// 데이터 바인딩을 통해 메인 뷰모델(MainViewModel)과 연결되며, 실제 이미지 삭제 등의 비즈니스 로직은 뷰모델에 전적으로 위임합니다.
    /// </summary>
    public partial class ImageManagerWindow : Window
    {
        /// <summary>
        /// 도커 이미지 관리 창을 초기화하고 XAML에 정의된 UI 구성요소들을 로드합니다.
        /// </summary>
        public ImageManagerWindow()
        {
            InitializeComponent();
        }
    }
}