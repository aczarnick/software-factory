using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

/// <summary>
/// Proves that a factory never has two toolchain invocations in flight at once, regardless of
/// item concurrency. Each check here holds the gate for long enough (via a fake compile step)
/// that a missing lock would show up as observed concurrency above one.
/// </summary>
public sealed class ToolchainGateTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    private sealed class NoRemediation : IRemediationRunner
    {
        public Task<RemediationResult> RemediateAsync(ToolchainRequirement requirement, CancellationToken ct = default) =>
            Task.FromResult(RemediationResult.NotFound);
    }

    private static string NewWorkDir(string root)
    {
        var dir = Path.Combine(root, Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "App.csproj"), "<Project/>");
        return dir;
    }

    [Fact]
    public async Task ToolchainGateSerialisesConcurrentItemChecks()
    {
        using var host = FactoryHost.Init(_dir, transport: new FakeTransport());

        var inFlight = 0;
        var peak = 0;
        var peakLock = new object();

        async Task<ShellResult> FakeCompile(ToolchainCheck check, string workDir, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref inFlight);
            lock (peakLock) peak = Math.Max(peak, current);
            await Task.Delay(150, ct).ConfigureAwait(false);
            Interlocked.Decrement(ref inFlight);
            return new ShellResult(0, "ok", "", false);
        }

        var station = new CheckStation(new NoRemediation(), execute: FakeCompile);

        StationContext ContextFor(WorkItem item) => new()
        {
            Services = host.Services,
            Def = new StationDef { Id = "check", Role = StationRole.Check },
            Run = new ItemRun(item, NewWorkDir(_dir))
        };

        var itemA = WorkItem.Create("first concurrent item");
        var itemB = WorkItem.Create("second concurrent item");

        await Task.WhenAll(
            station.ExecuteAsync(ContextFor(itemA)),
            station.ExecuteAsync(ContextFor(itemB)));

        Assert.Equal(1, peak);
    }
}
