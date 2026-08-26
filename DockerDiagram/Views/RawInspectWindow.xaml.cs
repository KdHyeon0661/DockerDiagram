using DockerDiagram.ApplicationServices;
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
            _defaultFileName = FileService.MakeSafeFileName(inspectTitle, "docker-inspect") + ".json";
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
    }
}
