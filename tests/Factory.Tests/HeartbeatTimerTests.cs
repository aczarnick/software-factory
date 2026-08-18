using Factory.Runtime;

namespace Factory.Tests;

public class HeartbeatTimerTests
{
    [Fact]
    public void DefaultIntervalIsThreeSeconds()
    {
        using var timer = new HeartbeatTimer(() => Task.CompletedTask);

        Assert.Equal(TimeSpan.FromSeconds(3), timer.Interval);
    }

    [Fact]
    public void CustomIntervalOverridesDefault()
    {
        var interval = TimeSpan.FromMilliseconds(25);
        using var timer = new HeartbeatTimer(() => Task.CompletedTask, interval);

        Assert.Equal(interval, timer.Interval);
    }

    [Fact]
    public async Task StartInvokesWriteHeartbeatRepeatedlyUntilStopped()
    {
        var count = 0;
        using var timer = new HeartbeatTimer(() =>
        {
            Interlocked.Increment(ref count);
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(20));

        timer.Start();
        await WaitUntilAsync(() => Volatile.Read(ref count) >= 3);
        timer.Stop();

        Assert.True(Volatile.Read(ref count) >= 3);
    }

    [Fact]
    public async Task StopHaltsFurtherInvocations()
    {
        var count = 0;
        using var timer = new HeartbeatTimer(() =>
        {
            Interlocked.Increment(ref count);
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(20));

        timer.Start();
        await WaitUntilAsync(() => Volatile.Read(ref count) >= 1);
        timer.Stop();

        var afterStop = Volatile.Read(ref count);
        await Task.Delay(100);

        Assert.Equal(afterStop, Volatile.Read(ref count));
    }

    [Fact]
    public void DisposeMultipleTimesNeverThrows()
    {
        var timer = new HeartbeatTimer(() => Task.CompletedTask, TimeSpan.FromMilliseconds(20));

        timer.Start();
        timer.Dispose();
        timer.Dispose();
        timer.Dispose();
    }

    [Fact]
    public void DisposeWithoutStartNeverThrows()
    {
        var timer = new HeartbeatTimer(() => Task.CompletedTask);

        timer.Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }
}
