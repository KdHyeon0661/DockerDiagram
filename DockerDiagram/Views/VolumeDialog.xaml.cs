using DockerDiagram.Contracts;
using DockerDiagram.Models;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace DockerDiagram.Views
{
    /// <summary>
    /// 새 Docker 볼륨을 생성하거나 외부 볼륨을 참조하기 위한 옵션을 입력받는 대화상자입니다.
    /// </summary>
    public partial class VolumeDialog : Window
    {
        private readonly IDialogService _dialogService;

        public string VolumeName => txtName.Text.Trim();

        public string Driver => txtDriver.Text.Trim();

        public VolumeCreateOptions CreateOptions => new()
        {
            Name = VolumeName,
            DockerVolumeName = txtDockerVolumeName.Text.Trim(),
            Driver = string.IsNullOrWhiteSpace(Driver) ? "local" : Driver,
            External = chkExternal.IsChecked == true,
            Labels = ParseKeyValueLines(txtLabels.Text),
            DriverOptions = ParseKeyValueLines(txtDriverOptions.Text)
        };

        public VolumeDialog(IDialogService dialogService, VolumeCreateOptions? initialOptions = null)
        {
            InitializeComponent();
            _dialogService = dialogService;

            if (initialOptions != null)
            {
                Title = "Edit Volume Options";
                btnOk.Content = "Apply";
                txtName.Text = initialOptions.Name;
                txtDockerVolumeName.Text = initialOptions.DockerVolumeName;
                txtDriver.Text = initialOptions.Driver;
                chkExternal.IsChecked = initialOptions.External;
                txtLabels.Text = FormatKeyValueLines(initialOptions.Labels);
                txtDriverOptions.Text = FormatKeyValueLines(initialOptions.DriverOptions);
            }

            txtName.Focus();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(VolumeName))
            {
                _dialogService.ShowError("볼륨 이름을 입력하세요.", "입력 오류");
                return;
            }

            if (!ValidateKeyValueLines(txtLabels.Text, "Labels")) return;
            if (!ValidateKeyValueLines(txtDriverOptions.Text, "Driver Options")) return;

            DialogResult = true;
        }

        private bool ValidateKeyValueLines(string text, string title)
        {
            foreach (var line in GetMeaningfulLines(text))
            {
                int index = line.IndexOf('=');
                if (index <= 0)
                {
                    _dialogService.ShowError(
                        $"{title} 항목은 key=value 형식이어야 합니다.\n문제 항목: {line}",
                        "입력 오류");
                    return false;
                }
            }

            return true;
        }

        private static Dictionary<string, string> ParseKeyValueLines(string text)
        {
            var result = new Dictionary<string, string>();
            foreach (var line in GetMeaningfulLines(text))
            {
                int index = line.IndexOf('=');
                if (index <= 0) continue;
                result[line[..index].Trim()] = line[(index + 1)..].Trim();
            }
            return result;
        }

        private static string FormatKeyValueLines(Dictionary<string, string> values)
        {
            return values == null || values.Count == 0
                ? string.Empty
                : string.Join("\n", values.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        private static IEnumerable<string> GetMeaningfulLines(string text)
        {
            return (text ?? string.Empty)
                .Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line));
        }
    }
}
