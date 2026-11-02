using System.Windows;
using DockerDiagram.Common;
using DockerDiagram.Models;

namespace DockerDiagram.Views
{
    public partial class SwarmDataResourceDialog : Window
    {
        private readonly SwarmDataResourceKind _kind;
        public SwarmDataResourceCreateOptions? CreateOptions { get; private set; }

        public SwarmDataResourceDialog(SwarmDataResourceKind kind)
        {
            InitializeComponent();
            _kind = kind;
            string label = kind.ToString();
            Title = $"Swarm {label} 생성";
            HeaderTextBlock.Text = $"Swarm {label} 생성";
            DescriptionTextBlock.Text = kind == SwarmDataResourceKind.Secret
                ? "민감한 데이터를 Swarm Secret으로 생성하고 서비스에 안전하게 참조합니다."
                : "설정 파일 또는 텍스트를 Swarm Config로 생성하고 서비스에 참조합니다.";
            NameTextBox.Text = kind == SwarmDataResourceKind.Secret ? "app-secret" : "app-config";
            CreateButton.Content = $"{label} 생성";
        }

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!KeyValueTextParser.TryParse(
                        LabelsTextBox.Text,
                        out Dictionary<string, string> labels,
                        out KeyValueTextError? labelError,
                        comparer: StringComparer.Ordinal,
                        rejectDuplicateKeys: true))
                {
                    throw labelError!.Kind == KeyValueTextErrorKind.DuplicateKey
                        ? new ArgumentException($"중복된 label key입니다: {labelError.Key}")
                        : new ArgumentException($"Label은 key=value 형식이어야 합니다: {labelError.Entry}");
                }

                var options = new SwarmDataResourceCreateOptions
                {
                    Kind = _kind,
                    Name = NameTextBox.Text.Trim(),
                    Data = DataTextBox.Text,
                    Labels = labels
                };
                options.Validate();
                CreateOptions = options;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                ValidationTextBlock.Text = ex.Message;
            }
        }
    }
}
