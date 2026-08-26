using System.Windows;

namespace DockerDiagram.Views
{
    public partial class VolumeDetailWindow : Window
    {
        public VolumeDetailWindow()
        {
            InitializeComponent();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
