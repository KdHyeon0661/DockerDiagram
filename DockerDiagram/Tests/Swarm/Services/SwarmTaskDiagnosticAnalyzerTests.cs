using DockerDiagram.ApplicationServices;
using DockerDiagram.Models;
using Xunit;

namespace DockerDiagram.Tests
{
    public sealed class SwarmTaskDiagnosticAnalyzerTests
    {
        [Theory]
        [InlineData("rejected", "no suitable node (scheduling constraints not satisfied)", SwarmTaskFailureCategory.SchedulingFailure)]
        [InlineData("rejected", "pull access denied for private/image", SwarmTaskFailureCategory.ImagePullFailure)]
        [InlineData("rejected", "port is already allocated", SwarmTaskFailureCategory.PortConflict)]
        [InlineData("orphaned", "", SwarmTaskFailureCategory.Orphaned)]
        [InlineData("running", "", SwarmTaskFailureCategory.Healthy)]
        [InlineData("preparing", "", SwarmTaskFailureCategory.Starting)]
        public void Classify_RecognizesOperationalFailure(string state, string error, SwarmTaskFailureCategory expected)
        {
            SwarmTaskFailureCategory category = SwarmTaskDiagnosticAnalyzer.Classify(
                Task("task", slot: 1, state: state, error: error));

            Assert.Equal(expected, category);
        }

        [Fact]
        public void Classify_RecognizesNonZeroProcessExit()
        {
            var task = Task("task", 1, "failed");
            task = new SwarmTaskDiagnostic
            {
                TaskId = task.TaskId,
                Slot = task.Slot,
                CurrentState = task.CurrentState,
                ExitCode = 137
            };

            Assert.Equal(SwarmTaskFailureCategory.ProcessExit, SwarmTaskDiagnosticAnalyzer.Classify(task));
        }

        [Fact]
        public void Analyze_AssignsAttemptsAndMarksOldReplicaTaskHistorical()
        {
            SwarmTaskDiagnostic oldTask = Task("old", 1, "shutdown", createdMinutes: 0);
            SwarmTaskDiagnostic currentTask = Task("current", 1, "running", createdMinutes: 1);

            IReadOnlyList<SwarmTaskDiagnostic> result = SwarmTaskDiagnosticAnalyzer.Analyze(new[] { currentTask, oldTask });

            Assert.True(oldTask.IsHistorical);
            Assert.Equal(1, oldTask.AttemptNumber);
            Assert.False(currentTask.IsHistorical);
            Assert.Equal(2, currentTask.AttemptNumber);
            Assert.Equal("current", result[0].TaskId);
        }

        [Fact]
        public void Analyze_KeepsGlobalTasksOnDifferentNodesCurrent()
        {
            SwarmTaskDiagnostic first = Task("first", 0, "running", nodeId: "node-a");
            SwarmTaskDiagnostic second = Task("second", 0, "running", nodeId: "node-b");

            SwarmTaskDiagnosticAnalyzer.Analyze(new[] { first, second });

            Assert.False(first.IsHistorical);
            Assert.False(second.IsHistorical);
            Assert.Equal(1, first.AttemptNumber);
            Assert.Equal(1, second.AttemptNumber);
        }

        [Fact]
        public void ReportSummary_CountsOnlyCurrentFailures()
        {
            SwarmTaskDiagnostic oldFailed = Task("old", 1, "failed", createdMinutes: 0);
            SwarmTaskDiagnostic running = Task("new", 1, "running", createdMinutes: 1);
            IReadOnlyList<SwarmTaskDiagnostic> analyzed = SwarmTaskDiagnosticAnalyzer.Analyze(new[] { oldFailed, running });
            var report = new SwarmTaskDiagnosticReport { Tasks = analyzed };

            Assert.Equal(1, report.CurrentCount);
            Assert.Equal(1, report.RunningCount);
            Assert.Equal(0, report.FailureCount);
            Assert.Equal(1, report.HistoricalCount);
        }

        [Fact]
        public void UnassignedPendingTask_IsSchedulingPending()
        {
            SwarmTaskDiagnostic task = Task("pending", 1, "pending", nodeId: string.Empty);

            Assert.Equal(
                SwarmTaskFailureCategory.SchedulingPending,
                SwarmTaskDiagnosticAnalyzer.Classify(task));
        }

        private static SwarmTaskDiagnostic Task(
            string id,
            ulong slot,
            string state,
            string error = "",
            int createdMinutes = 0,
            string nodeId = "node-a") =>
            new()
            {
                TaskId = id,
                Slot = slot,
                NodeId = nodeId,
                NodeName = string.IsNullOrWhiteSpace(nodeId) ? "Unassigned" : nodeId,
                DesiredState = "running",
                CurrentState = state,
                Error = error,
                CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddMinutes(createdMinutes)
            };
    }
}
