using System.Windows;

namespace DockerDiagram.Views
{
    public partial class DockerContextCreateDialog : Window
    {
        private readonly DockerDiagram.Contracts.IDialogService _dialogService;

        public DockerContextCreateDialog(DockerDiagram.Contracts.IDialogService dialogService)
        {
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            InitializeComponent();
        }

        public string ContextName => NameBox.Text.Trim();
        public string DockerEndpoint => EndpointBox.Text.Trim();

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ContextName))
            {
                _dialogService.ShowInfo("Context 이름을 입력해 주세요.", "Docker Context 생성");
                return;
            }

            if (string.IsNullOrWhiteSpace(DockerEndpoint))
            {
                _dialogService.ShowInfo("Docker 호스트 엔드포인트를 입력해 주세요.", "Docker Context 생성");
                return;
            }

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
