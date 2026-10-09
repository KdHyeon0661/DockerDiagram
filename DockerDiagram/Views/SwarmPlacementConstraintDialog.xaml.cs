using DockerDiagram.Models;
using System.Windows;

namespace DockerDiagram.Views
{
    public partial class SwarmPlacementConstraintDialog : Window
    {
        private readonly SwarmServicePlacementSnapshot _snapshot;

        public SwarmPlacementConstraintDialog(SwarmServicePlacementSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            InitializeComponent();
            _snapshot = snapshot;
            DescriptionTextBlock.Text = $"'{snapshot.ServiceName}' · version {snapshot.Version}";
            ConstraintsTextBox.Text = string.Join(Environment.NewLine, snapshot.Constraints);
        }

        public SwarmServicePlacementUpdateOptions? UpdateOptions { get; private set; }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string[] constraints = ConstraintsTextBox.Text.Split(
                    new[] { "\r\n", "\n" },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var options = new SwarmServicePlacementUpdateOptions
                {
                    ServiceId = _snapshot.ServiceId,
                    Version = _snapshot.Version,
                    Constraints = constraints
                };
                options.Validate();
                UpdateOptions = options;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                ValidationTextBlock.Text = ex.Message;
            }
        }
    }
}
