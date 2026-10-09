using DockerDiagram.ApplicationServices;
using DockerDiagram.Models;
using System.Windows;

namespace DockerDiagram.Views
{
    public partial class SwarmResourceReferenceDialog : Window
    {
        public SwarmResourceTargetOptions Options { get; private set; }

        public SwarmResourceReferenceDialog(
            SwarmDataResourceKind kind,
            string resourceName,
            SwarmResourceTargetOptions initialOptions)
        {
            InitializeComponent();
            Options = initialOptions;

            string kindLabel = kind.ToString();
            Title = $"Attach Swarm {kindLabel}";
            HeaderTextBlock.Text = $"Attach {kindLabel}: {resourceName}";
            DescriptionTextBlock.Text =
                "서비스 컨테이너에서 이 리소스를 어떤 파일로 사용할지 설정합니다.";
            HelpTextBlock.Text = kind == SwarmDataResourceKind.Secret
                ? "Secret의 단순 파일명은 기본적으로 /run/secrets 아래에 배치됩니다. 절대 경로도 지정할 수 있습니다. UID/GID는 숫자 또는 컨테이너의 사용자·그룹 이름이며, 기본 Mode는 읽기 전용 0444입니다."
                : "Config의 단순 파일명은 Linux 컨테이너의 / 아래에 배치됩니다. /etc/app/config.yml 같은 절대 경로도 지정할 수 있습니다. 기본 Mode는 읽기 전용 0444입니다.";

            TargetTextBox.Text = initialOptions.FileName;
            UidTextBox.Text = initialOptions.Uid;
            GidTextBox.Text = initialOptions.Gid;
            ModeTextBox.Text = SwarmResourceReferenceInputParser.FormatMode(initialOptions.Mode);
        }

        private void AttachButton_Click(object sender, RoutedEventArgs e)
        {
            if (!SwarmResourceReferenceInputParser.TryParse(
                    TargetTextBox.Text,
                    UidTextBox.Text,
                    GidTextBox.Text,
                    ModeTextBox.Text,
                    out SwarmResourceTargetOptions options,
                    out string error))
            {
                ValidationTextBlock.Text = error;
                return;
            }

            Options = options;
            DialogResult = true;
        }
    }
}
