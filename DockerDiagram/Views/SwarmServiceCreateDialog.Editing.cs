using DockerDiagram.ApplicationServices;
using DockerDiagram.Models;
using System.Windows.Controls;

namespace DockerDiagram.Views
{
    public partial class SwarmServiceCreateDialog
    {
        public SwarmServiceCreateDialog(SwarmServiceSpecOptions initialSpec) : this()
        {
            ArgumentNullException.ThrowIfNull(initialSpec);
            SwarmServiceCreateFormInput input = SwarmServiceEditFormatter.ToFormInput(initialSpec);

            Title = "Edit Swarm Service";
            ServiceNameTextBox.Text = input.Name;
            ServiceNameTextBox.IsReadOnly = true;
            ServiceNameTextBox.ToolTip = "Docker Swarm service 이름은 생성 후 변경할 수 없습니다.";
            ImageTextBox.Text = input.Image;
            SelectComboTag(ModeComboBox, input.Mode);
            ModeComboBox.IsEnabled = false;
            ModeComboBox.ToolTip = "Replicated/global mode는 생성 후 변경할 수 없습니다.";
            ReplicasTextBox.Text = input.Replicas;
            ReplicasTextBox.IsEnabled = initialSpec.Mode == SwarmServiceModeKind.Replicated;
            CommandTextBox.Text = input.Command;
            ArgumentsTextBox.Text = input.Arguments;
            EnvironmentTextBox.Text = input.EnvironmentVariables;
            LabelsTextBox.Text = input.Labels;
            PortsTextBox.Text = input.PublishedPorts;
            NetworksTextBox.Text = input.Networks;
            MountsTextBox.Text = input.Mounts;
            SelectComboTag(RestartConditionComboBox, input.RestartCondition);
            RestartDelayTextBox.Text = input.RestartDelaySeconds;
            RestartMaxAttemptsTextBox.Text = input.RestartMaxAttempts;
            RestartWindowTextBox.Text = input.RestartWindowSeconds;
            UpdateParallelismTextBox.Text = input.UpdateParallelism;
            UpdateDelayTextBox.Text = input.UpdateDelaySeconds;
            UpdateMonitorTextBox.Text = input.UpdateMonitorSeconds;
            UpdateRatioTextBox.Text = input.UpdateMaxFailureRatio;
            SelectComboTag(UpdateActionComboBox, input.UpdateFailureAction);
            SelectComboTag(UpdateOrderComboBox, input.UpdateOrder);
            RollbackParallelismTextBox.Text = input.RollbackParallelism;
            RollbackDelayTextBox.Text = input.RollbackDelaySeconds;
            RollbackMonitorTextBox.Text = input.RollbackMonitorSeconds;
            RollbackRatioTextBox.Text = input.RollbackMaxFailureRatio;
            SelectComboTag(RollbackActionComboBox, input.RollbackFailureAction);
            SelectComboTag(RollbackOrderComboBox, input.RollbackOrder);

            DialogTitleTextBlock.Text = "Edit Swarm Service";
            DialogDescriptionTextBlock.Text = "현재 service version을 기준으로 설정을 안전하게 갱신합니다.";
            SubmitButton.Content = "Apply Changes";
            PlacementResourcesTab.Visibility = System.Windows.Visibility.Collapsed;
        }

        private static void SelectComboTag(ComboBox comboBox, string tag)
        {
            foreach (ComboBoxItem item in comboBox.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedItem = item;
                    return;
                }
            }
        }

    }
}
