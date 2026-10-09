using DockerDiagram.Models;

namespace DockerDiagram.ApplicationServices
{
    public static class SwarmTaskDiagnosticAnalyzer
    {
        public static IReadOnlyList<SwarmTaskDiagnostic> Analyze(IEnumerable<SwarmTaskDiagnostic> tasks)
        {
            SwarmTaskDiagnostic[] materialized = tasks.ToArray();
            foreach (IGrouping<string, SwarmTaskDiagnostic> group in materialized.GroupBy(AttemptGroupKey))
            {
                SwarmTaskDiagnostic[] attempts = group
                    .OrderBy(task => task.CreatedAt ?? DateTimeOffset.MinValue)
                    .ThenBy(task => task.TaskId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                for (int index = 0; index < attempts.Length; index++)
                {
                    attempts[index].AttemptNumber = index + 1;
                    attempts[index].IsHistorical = index < attempts.Length - 1;
                    attempts[index].Category = Classify(attempts[index]);
                    attempts[index].StatusColor = ColorFor(attempts[index].Category, attempts[index].IsHistorical);
                    if (attempts[index].IsHistorical && attempts[index].Category is
                        SwarmTaskFailureCategory.Healthy or
                        SwarmTaskFailureCategory.Starting or
                        SwarmTaskFailureCategory.Completed or
                        SwarmTaskFailureCategory.Unknown)
                    {
                        attempts[index].Category = SwarmTaskFailureCategory.Historical;
                        attempts[index].StatusColor = ColorFor(SwarmTaskFailureCategory.Historical, true);
                    }
                }
            }

            return materialized
                .OrderBy(task => task.Slot)
                .ThenBy(task => task.IsHistorical ? 1 : 0)
                .ThenByDescending(task => task.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(task => task.TaskId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static SwarmTaskFailureCategory Classify(SwarmTaskDiagnostic task)
        {
            string state = task.CurrentState.Trim().ToLowerInvariant();
            string text = $"{task.Error} {task.Message}".ToLowerInvariant();

            if (state == "orphaned") return SwarmTaskFailureCategory.Orphaned;
            if (ContainsImagePullFailure(text)) return SwarmTaskFailureCategory.ImagePullFailure;
            if (ContainsPortConflict(text)) return SwarmTaskFailureCategory.PortConflict;
            if (ContainsSchedulingFailure(text)) return SwarmTaskFailureCategory.SchedulingFailure;
            if (state == "rejected") return SwarmTaskFailureCategory.Rejected;
            if (state == "failed")
                return task.ExitCode != 0 ? SwarmTaskFailureCategory.ProcessExit : SwarmTaskFailureCategory.Failed;
            if (state == "shutdown" && (task.ExitCode != 0 || !string.IsNullOrWhiteSpace(task.Error)))
                return task.ExitCode != 0 ? SwarmTaskFailureCategory.ProcessExit : SwarmTaskFailureCategory.Failed;
            if (state == "running") return SwarmTaskFailureCategory.Healthy;
            if (state == "complete") return SwarmTaskFailureCategory.Completed;
            if (state == "remove") return SwarmTaskFailureCategory.Removed;
            if (string.IsNullOrWhiteSpace(task.NodeId) && state is "new" or "pending")
                return SwarmTaskFailureCategory.SchedulingPending;
            if (state is "new" or "pending" or "assigned" or "accepted" or "preparing" or "ready" or "starting")
                return SwarmTaskFailureCategory.Starting;
            if (state == "shutdown") return SwarmTaskFailureCategory.Historical;
            return SwarmTaskFailureCategory.Unknown;
        }

        private static string AttemptGroupKey(SwarmTaskDiagnostic task) =>
            task.Slot > 0
                ? $"slot:{task.Slot}"
                : !string.IsNullOrWhiteSpace(task.NodeId)
                    ? $"global:{task.NodeId}"
                    : $"unassigned:{task.TaskId}";

        private static bool ContainsImagePullFailure(string text) =>
            text.Contains("pull access denied", StringComparison.Ordinal) ||
            text.Contains("no such image", StringComparison.Ordinal) ||
            text.Contains("manifest unknown", StringComparison.Ordinal) ||
            text.Contains("failed to resolve reference", StringComparison.Ordinal) ||
            text.Contains("image pull", StringComparison.Ordinal);

        private static bool ContainsPortConflict(string text) =>
            text.Contains("port is already in use", StringComparison.Ordinal) ||
            text.Contains("address already in use", StringComparison.Ordinal) ||
            text.Contains("port is already allocated", StringComparison.Ordinal) ||
            text.Contains("endpoint with name", StringComparison.Ordinal);

        private static bool ContainsSchedulingFailure(string text) =>
            text.Contains("no suitable node", StringComparison.Ordinal) ||
            text.Contains("scheduling constraints", StringComparison.Ordinal) ||
            text.Contains("insufficient resources", StringComparison.Ordinal) ||
            text.Contains("unsupported platform", StringComparison.Ordinal);

        private static string ColorFor(SwarmTaskFailureCategory category, bool historical) =>
            historical ? "#7A858D" : category switch
            {
                SwarmTaskFailureCategory.Healthy or SwarmTaskFailureCategory.Completed => "#28A745",
                SwarmTaskFailureCategory.Starting or SwarmTaskFailureCategory.SchedulingPending => "#E6A700",
                SwarmTaskFailureCategory.Unknown or SwarmTaskFailureCategory.Historical or SwarmTaskFailureCategory.Removed => "#7A858D",
                _ => "#DC3545"
            };
    }
}
