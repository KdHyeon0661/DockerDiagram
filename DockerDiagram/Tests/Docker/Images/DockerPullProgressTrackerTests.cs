using Docker.DotNet.Models;
using DockerDiagram.Infrastructure;

namespace DockerDiagram.Tests;

public sealed class DockerPullProgressTrackerTests
{
    [Fact]
    public void Update_WithoutLayerProgress_ReportsStatusWithoutInventingPercent()
    {
        var tracker = new DockerPullProgressTracker();

        DockerPullProgressSnapshot snapshot = tracker.Update(new JSONMessage
        {
            Status = "Pulling manifest"
        });

        Assert.Equal("Pulling manifest", snapshot.Message);
        Assert.Null(snapshot.Percent);
    }

    [Fact]
    public void Update_AggregatesProgressAcrossLayersAndClampsCompletedBytes()
    {
        var tracker = new DockerPullProgressTracker();
        tracker.Update(Message("layer-one", 50, 100));

        DockerPullProgressSnapshot snapshot = tracker.Update(Message("layer-two", 150, 100));

        Assert.Equal(75, snapshot.Percent);
        Assert.Contains("75%", snapshot.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_PrioritizesDockerErrorMessage()
    {
        var tracker = new DockerPullProgressTracker();

        DockerPullProgressSnapshot snapshot = tracker.Update(new JSONMessage
        {
            Status = "Downloading",
            ErrorMessage = "pull access denied"
        });

        Assert.Equal("pull access denied", snapshot.Message);
    }

    private static JSONMessage Message(string id, long current, long total) => new()
    {
        ID = id,
        Status = "Downloading",
        Progress = new JSONProgress
        {
            Current = current,
            Total = total
        }
    };
}
