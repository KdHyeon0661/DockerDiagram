using Docker.DotNet.Models;
using DockerDiagram.ApplicationServices;
using DockerDiagram.Models;

namespace DockerDiagram.Tests;

public sealed class SwarmRuntimeSynchronizationTests
{
    [Fact]
    public async Task RequestPump_CoalescesManyRequestsDuringActiveRun()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;
        using var pump = new CoalescingAsyncRequestPump(async () =>
        {
            int current = Interlocked.Increment(ref runs);
            if (current == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
            }
        });

        Task first = pump.RequestAsync();
        await firstStarted.Task;
        Task[] queued = Enumerable.Range(0, 20).Select(_ => pump.RequestAsync()).ToArray();
        releaseFirst.SetResult();
        await Task.WhenAll(queued.Append(first));

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task RequestPump_DoesNotRunAfterDispose()
    {
        int runs = 0;
        var pump = new CoalescingAsyncRequestPump(() =>
        {
            runs++;
            return Task.CompletedTask;
        });
        pump.Dispose();

        await pump.RequestAsync();

        Assert.Equal(0, runs);
    }

    [Theory]
    [InlineData("service")]
    [InlineData("task")]
    [InlineData("node")]
    [InlineData("secret")]
    [InlineData("config")]
    [InlineData("swarm")]
    public void DockerEventFilter_IncludesSwarmResourceEvents(string type)
    {
        Assert.True(DockerSyncCoordinator.IsDiagramRelevantDockerEvent(new Message
        {
            Type = type,
            Action = "update"
        }));
    }

    [Fact]
    public void DockerEventFilter_IgnoresExecNoise()
    {
        Assert.False(DockerSyncCoordinator.IsDiagramRelevantDockerEvent(new Message
        {
            Type = "container",
            Action = "exec_start"
        }));
    }

    [Fact]
    public void IdentityMatcher_PrefersCurrentId()
    {
        var resources = new[]
        {
            new Identity("new", "api"),
            new Identity("old", "renamed")
        };

        SwarmIdentityMatch<Identity> result = SwarmRuntimeIdentityMatcher.Match(
            resources, "old", "api", item => item.Id, item => item.Name);

        Assert.Equal("old", result.Resource?.Id);
        Assert.False(result.ReboundByName);
    }

    [Fact]
    public void IdentityMatcher_RebindsUniqueNameAfterResourceRecreation()
    {
        var resources = new[] { new Identity("new-id", "api") };

        SwarmIdentityMatch<Identity> result = SwarmRuntimeIdentityMatcher.Match(
            resources, "deleted-id", "API", item => item.Id, item => item.Name);

        Assert.Equal("new-id", result.Resource?.Id);
        Assert.True(result.ReboundByName);
    }

    [Fact]
    public void IdentityMatcher_RejectsAmbiguousNameFallback()
    {
        var resources = new[]
        {
            new Identity("one", "api"),
            new Identity("two", "API")
        };

        SwarmIdentityMatch<Identity> result = SwarmRuntimeIdentityMatcher.Match(
            resources, "missing", "api", item => item.Id, item => item.Name);

        Assert.Null(result.Resource);
        Assert.False(result.ReboundByName);
    }

    [Fact]
    public void PartialSyncState_ParsesAndDeduplicatesResources()
    {
        IReadOnlyList<string> failures = SwarmPartialSyncState.Parse(
            "Partial sync: Services, Nodes, services");

        Assert.Equal(new[] { "Services", "Nodes" }, failures);
    }

    [Fact]
    public void PartialSyncState_IgnoresHealthyStatus()
    {
        Assert.Empty(SwarmPartialSyncState.Parse("Last updated: 12:34:56"));
    }

    private sealed record Identity(string Id, string Name);
}

