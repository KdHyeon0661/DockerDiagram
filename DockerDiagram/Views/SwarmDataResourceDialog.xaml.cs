using DockerDiagram.Models;
using System.Windows;

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
            Title = $"Create Swarm {label}";
            HeaderTextBlock.Text = $"Create Swarm {label}";
            DescriptionTextBlock.Text = kind == SwarmDataResourceKind.Secret
                ? "민감한 데이터를 Swarm Secret으로 생성하고 서비스에 안전하게 참조합니다."
                : "설정 파일 또는 텍스트를 Swarm Config로 생성하고 서비스에 참조합니다.";
            NameTextBox.Text = kind == SwarmDataResourceKind.Secret ? "app-secret" : "app-config";
            CreateButton.Content = $"Create {label}";
        }

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var labels = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string line in LabelsTextBox.Text.Split(
                             new[] { "\r\n", "\n" },
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0)
                        throw new ArgumentException($"Label은 key=value 형식이어야 합니다: {line}");
                    string key = line[..separator].Trim();
                    if (!labels.TryAdd(key, line[(separator + 1)..].Trim()))
                        throw new ArgumentException($"중복된 label key입니다: {key}");
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
