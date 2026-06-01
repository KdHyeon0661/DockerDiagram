using Microsoft.Win32;
using System;
using System.IO;
using System.Windows;

namespace DockerDiagram.Views
{
    public partial class RawInspectWindow : Window
    {
        private readonly string _defaultFileName;

        public RawInspectWindow(string inspectTitle, string json)
        {
            InitializeComponent();
            Title = inspectTitle;
            TitleText.Text = inspectTitle;
            JsonTextBox.Text = json;
            _defaultFileName = MakeSafeFileName(inspectTitle) + ".json";
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(JsonTextBox.Text);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save Raw Inspect JSON",
                Filter = "JSON file (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = ".json",
                FileName = _defaultFileName
            };

            if (dialog.ShowDialog(this) == true)
            {
                File.WriteAllText(dialog.FileName, JsonTextBox.Text);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private static string MakeSafeFileName(string value)
        {
            foreach (var invalidChar in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalidChar, '_');
            }

            return string.IsNullOrWhiteSpace(value) ? "docker-inspect" : value;
        }
    }
}
