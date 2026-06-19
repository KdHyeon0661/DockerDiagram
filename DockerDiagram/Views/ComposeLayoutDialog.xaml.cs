using DockerDiagram.Models;
using System.Windows;

namespace DockerDiagram.Views
{
    public partial class ComposeLayoutDialog : Window
    {
        public ComposeLayoutOptions Options { get; private set; }

        public ComposeLayoutDialog(ComposeLayoutOptions initialOptions)
        {
            InitializeComponent();
            Options = initialOptions.Clone();
            LeftToRightRadio.IsChecked = initialOptions.Direction == ComposeLayoutDirection.LeftToRight;
            TopToBottomRadio.IsChecked = initialOptions.Direction == ComposeLayoutDirection.TopToBottom;
            HorizontalGapSlider.Value = initialOptions.HorizontalGap;
            VerticalGapSlider.Value = initialOptions.VerticalGap;
            AdaptiveSpacingCheckBox.IsChecked = initialOptions.UseAdaptiveSpacing;
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            Options = new ComposeLayoutOptions
            {
                Direction = TopToBottomRadio.IsChecked == true
                    ? ComposeLayoutDirection.TopToBottom
                    : ComposeLayoutDirection.LeftToRight,
                HorizontalGap = HorizontalGapSlider.Value,
                VerticalGap = VerticalGapSlider.Value,
                UseAdaptiveSpacing = AdaptiveSpacingCheckBox.IsChecked == true
            };
            DialogResult = true;
        }
    }
}
