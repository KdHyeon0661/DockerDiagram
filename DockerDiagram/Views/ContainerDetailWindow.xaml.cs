using System.Windows;

namespace DockerDiagram
{
    public partial class ContainerDetailWindow : Window
    {
        public ContainerDetailWindow()
        {
            InitializeComponent();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}