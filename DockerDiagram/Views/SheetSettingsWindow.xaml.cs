using System.Globalization;
using System.Windows;
using DockerDiagram.Contracts;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;

namespace DockerDiagram.Views
{
    public partial class SheetSettingsWindow : Window
    {
        private readonly SheetViewModel _sheet;
        private readonly IDialogService _dialogService;

        public bool SheetSizeChanged { get; private set; }

        public SheetSettingsWindow(MainViewModel mainViewModel, IDialogService dialogService)
        {
            ArgumentNullException.ThrowIfNull(mainViewModel);
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            _sheet = mainViewModel.ActiveSheet ??
                     throw new InvalidOperationException("활성화된 시트가 없습니다.");

            InitializeComponent();
            DataContext = mainViewModel;

            SheetWidthTextBox.Text = (_sheet.MapWidth / SheetViewModel.MapInputScale)
                .ToString(CultureInfo.CurrentCulture);
            SheetHeightTextBox.Text = (_sheet.MapHeight / SheetViewModel.MapInputScale)
                .ToString(CultureInfo.CurrentCulture);
            HistoryOptionsCard.Visibility = _sheet.RuntimeKind == RuntimeKind.DockerEngine
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            bool validWidth = double.TryParse(
                SheetWidthTextBox.Text.Trim(),
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out double width) && double.IsFinite(width);
            bool validHeight = double.TryParse(
                SheetHeightTextBox.Text.Trim(),
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out double height) && double.IsFinite(height);

            if (!validWidth || !validHeight)
            {
                _dialogService.ShowError("너비와 높이는 숫자로 입력해 주세요.", "시트 설정");
                return;
            }

            if (width < SheetViewModel.MinimumMapInputWidth ||
                height < SheetViewModel.MinimumMapInputHeight)
            {
                _dialogService.ShowError(
                    $"시트 크기는 너비 {SheetViewModel.MinimumMapInputWidth:0} 이상, " +
                    $"높이 {SheetViewModel.MinimumMapInputHeight:0} 이상이어야 합니다.",
                    "시트 설정");
                return;
            }

            double mapWidth = width * SheetViewModel.MapInputScale;
            double mapHeight = height * SheetViewModel.MapInputScale;
            if (!double.IsFinite(mapWidth) || !double.IsFinite(mapHeight))
            {
                _dialogService.ShowError("입력한 시트 크기가 너무 큽니다.", "시트 설정");
                return;
            }

            SheetSizeChanged = Math.Abs(_sheet.MapWidth - mapWidth) > 0.001 ||
                               Math.Abs(_sheet.MapHeight - mapHeight) > 0.001;
            if (SheetSizeChanged)
            {
                _sheet.MapWidth = mapWidth;
                _sheet.MapHeight = mapHeight;
            }

            DialogResult = true;
        }
    }
}
