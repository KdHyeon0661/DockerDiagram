using System.Windows;

namespace DockerDiagram.Views
{
    public partial class ContainerRenameWindow : Window
    {
        private readonly DockerDiagram.Contracts.IDialogService _dialogService;
        public string NewName => NameBox.Text.Trim();

        public ContainerRenameWindow(DockerDiagram.Contracts.IDialogService dialogService, string currentName)
        {
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            InitializeComponent();
            NameBox.Text = currentName;
            NameBox.Focus();
            NameBox.SelectAll();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(NewName))
            {
                _dialogService.ShowInfo("컨테이너 이름을 입력해 주세요.", "컨테이너 이름 변경");
                return;
            }

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
