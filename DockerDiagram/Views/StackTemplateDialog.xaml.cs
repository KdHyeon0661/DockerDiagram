using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DockerDiagram.Models;

namespace DockerDiagram.Views
{
    public partial class StackTemplateDialog : Window
    {
        private readonly StackTemplateDefinition _template;
        private readonly Dictionary<string, FrameworkElement> _inputs =
            new(StringComparer.OrdinalIgnoreCase);

        public StackTemplateDeploymentOptions DeploymentOptions { get; private set; } = new();

        public StackTemplateDialog(StackTemplateDefinition template, string? suggestedProjectName = null)
        {
            InitializeComponent();
            _template = template;

            TemplateNameText.Text = template.Name;
            TemplateDescriptionText.Text = template.Description;
            TemplateSummaryText.Text = template.ResourceSummary;
            ProjectNameBox.Text = string.IsNullOrWhiteSpace(suggestedProjectName)
                ? template.DefaultProjectName
                : suggestedProjectName;

            try
            {
                AccentBar.Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(template.AccentColor));
            }
            catch
            {
                AccentBar.Background = new SolidColorBrush(Color.FromRgb(40, 123, 174));
            }

            BuildVariableInputs();
        }

        private void BuildVariableInputs()
        {
            foreach (var variable in _template.Variables)
            {
                var container = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
                container.Children.Add(new TextBlock
                {
                    Text = variable.Label,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(58, 66, 76))
                });

                FrameworkElement input;
                if (variable.Type.Equals("bool", StringComparison.OrdinalIgnoreCase))
                {
                    input = new CheckBox
                    {
                        IsChecked = bool.TryParse(variable.DefaultValue, out bool enabled) && enabled,
                        Margin = new Thickness(0, 8, 0, 0),
                        Content = "Enabled"
                    };
                }
                else if (variable.Type.Equals("password", StringComparison.OrdinalIgnoreCase))
                {
                    input = new PasswordBox
                    {
                        Password = variable.DefaultValue,
                        Height = 34,
                        Padding = new Thickness(9, 5, 9, 5),
                        Margin = new Thickness(0, 7, 0, 0),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(204, 211, 218)),
                        BorderThickness = new Thickness(1)
                    };
                }
                else
                {
                    input = new TextBox
                    {
                        Text = variable.DefaultValue,
                        Height = 34,
                        Padding = new Thickness(9, 5, 9, 5),
                        Margin = new Thickness(0, 7, 0, 0),
                        VerticalContentAlignment = VerticalAlignment.Center,
                        BorderBrush = new SolidColorBrush(Color.FromRgb(204, 211, 218)),
                        BorderThickness = new Thickness(1)
                    };
                }

                _inputs[variable.Key] = input;
                container.Children.Add(input);
                VariablesPanel.Children.Add(container);
            }
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            string projectName = ProjectNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(projectName))
            {
                MessageBox.Show(this, "프로젝트 이름을 입력해 주세요.", "Stack Template", MessageBoxButton.OK, MessageBoxImage.Warning);
                ProjectNameBox.Focus();
                return;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var variable in _template.Variables)
            {
                string value = ReadValue(_inputs[variable.Key]);
                if (variable.Required && string.IsNullOrWhiteSpace(value))
                {
                    MessageBox.Show(this, $"'{variable.Label}' 값을 입력해 주세요.", "Stack Template", MessageBoxButton.OK, MessageBoxImage.Warning);
                    _inputs[variable.Key].Focus();
                    return;
                }

                if (variable.Type.Equals("port", StringComparison.OrdinalIgnoreCase) &&
                    (!int.TryParse(value, out int port) || port is < 1 or > 65535))
                {
                    MessageBox.Show(this, $"'{variable.Label}' 포트는 1~65535 사이여야 합니다.", "Stack Template", MessageBoxButton.OK, MessageBoxImage.Warning);
                    _inputs[variable.Key].Focus();
                    return;
                }

                values[variable.Key] = value;
            }

            DeploymentOptions = new StackTemplateDeploymentOptions
            {
                ProjectName = projectName,
                DeployToDocker = DeployToDockerCheckBox.IsChecked == true,
                Variables = values
            };

            DialogResult = true;
        }

        private static string ReadValue(FrameworkElement input)
        {
            return input switch
            {
                TextBox textBox => textBox.Text.Trim(),
                PasswordBox passwordBox => passwordBox.Password,
                CheckBox checkBox => (checkBox.IsChecked == true).ToString(),
                _ => string.Empty
            };
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
