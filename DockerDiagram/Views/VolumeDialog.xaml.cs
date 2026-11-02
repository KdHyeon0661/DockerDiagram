using System.Collections.Generic;
using System.Linq;
using System.Windows;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Common;
using DockerDiagram.Contracts;
using DockerDiagram.Models;

namespace DockerDiagram.Views
{
    internal enum VolumeDialogMode
    {
        DockerCreate,
        SwarmDefinition
    }

    /// <summary>
    /// 새 Docker 볼륨을 생성하거나 외부 볼륨을 참조하기 위한 옵션을 입력받는 대화상자입니다.
    /// </summary>
    public partial class VolumeDialog : Window
    {
        private readonly IDialogService _dialogService;
        private VolumeCreateOptions? _createOptions;

        public string VolumeName => txtName.Text.Trim();

        public string Driver => txtDriver.Text.Trim();

        public VolumeCreateOptions CreateOptions => _createOptions ??
            throw new InvalidOperationException("볼륨 입력이 아직 검증되지 않았습니다.");

        public VolumeDialog(
            IDialogService dialogService,
            VolumeCreateOptions? initialOptions = null)
            : this(dialogService, initialOptions, VolumeDialogMode.DockerCreate)
        {
        }

        internal VolumeDialog(
            IDialogService dialogService,
            VolumeCreateOptions? initialOptions,
            VolumeDialogMode mode)
        {
            InitializeComponent();
            _dialogService = dialogService;

            if (mode == VolumeDialogMode.SwarmDefinition)
            {
                Title = "Define Swarm Volume";
                btnOk.Content = "Add";
                SwarmDefinitionNotice.Visibility = Visibility.Visible;
                chkExternal.Content = "Existing/external volume reference";
                DockerVolumeNameLabel.Text = "원본 볼륨 이름 (선택):";
            }

            if (initialOptions != null)
            {
                Title = mode == VolumeDialogMode.SwarmDefinition
                    ? "Swarm 볼륨 정의 편집"
                    : "볼륨 옵션 편집";
                btnOk.Content = "적용";
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
            FormParseResult<VolumeCreateOptions> result = VolumeCreateFormValidator.Parse(
                new VolumeCreateFormInput(
                    VolumeName,
                    txtDockerVolumeName.Text,
                    Driver,
                    chkExternal.IsChecked == true,
                    txtLabels.Text,
                    txtDriverOptions.Text));
            if (!result.IsValid)
            {
                _dialogService.ShowError(result.Message, "입력 오류");
                return;
            }
            _createOptions = result.Value!;
            DialogResult = true;
        }

        private static string FormatKeyValueLines(Dictionary<string, string> values)
            => KeyValueTextParser.Format(values);
    }
}
