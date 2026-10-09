using DockerDiagram.ApplicationServices;

namespace DockerDiagram.Tests;

public sealed class CoalescingAsyncRequestPumpTests
{
    [Fact]
    public async Task RequestPump_AllowsSequentialRequestsAfterSynchronousCompletion()
    {
        int runs = 0;
        using var pump = new CoalescingAsyncRequestPump(() =>
        {
            runs++;
            return Task.CompletedTask;
        });

        await pump.RequestAsync();
        await pump.RequestAsync();
        await pump.RequestAsync();

        Assert.Equal(3, runs);
    }

    [Fact]
    public async Task RequestPump_RecoversAfterOperationFailure()
    {
        int runs = 0;
        using var pump = new CoalescingAsyncRequestPump(() =>
        {
            runs++;
            return runs == 1
                ? Task.FromException(new InvalidOperationException("first run failed"))
                : Task.CompletedTask;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => pump.RequestAsync());
        await pump.RequestAsync();

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task RequestPump_CompletesEachRequestAfterItsOwnCoalescedRun()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;
        using var pump = new CoalescingAsyncRequestPump(async () =>
        {
            int current = Interlocked.Increment(ref runs);
            if (current == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
            else if (current == 2)
            {
                secondStarted.TrySetResult();
                await releaseSecond.Task;
            }
        });

        Task first = pump.RequestAsync();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task second = pump.RequestAsync();

        releaseFirst.TrySetResult();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(second.IsCompleted);

        releaseSecond.TrySetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, runs);
    }
}
