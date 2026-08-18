using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

public class ToolchainRemediationTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    private sealed class FakeRemediationRunner(RemediationResult result) : IRemediationRunner
    {
        public int Calls { get; private set; }

        public Task<RemediationResult> RemediateAsync(ToolchainRequirement requirement, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    /// <summary>Returns each compatibility result in turn — one call for the initial
    /// detection, a second for the post-remediation re-check — repeating the last if called
    /// again, so a test never has to guess exactly how many times it will be probed.</summary>
    private sealed class FakeToolchainProbe(params ToolchainCompatibilityResult[] results) : IToolchainProbe
    {
        private int _next;

        public Task<ToolchainCompatibilityResult> ProbeAsync(string repoPath, CancellationToken ct = default) =>
            Task.FromResult(results[Math.Min(_next++, results.Length - 1)]);
    }

    private StationContext ContextFor(FactoryHost host, WorkItem item) => new()
    {
        Services = host.Services,
        Def = new StationDef { Id = "check", Role = StationRole.Check },
        Run = new ItemRun(item, _dir)
    };

    [Fact]
    public async Task RemediationSucceeds_ProceedsToRegularCheck()
    {
        using var host = FactoryHost.Init(_dir, transport: new FakeTransport());
        var item = WorkItem.Create("needs a newer toolchain");

        var runner = new FakeRemediationRunner(new RemediationResult(true, true, true, "installed", null));
        var mismatch = ToolchainCompatibilityResult.Incompatible(["9.0.100"], ["8.0.100"]);
        var resolved = ToolchainCompatibilityResult.Compatible();
        var station = new CheckStation(runner, new FakeToolchainProbe(mismatch, resolved));

        var result = await station.ExecuteAsync(ContextFor(host, item));

        Assert.Equal(1, runner.Calls);
        // The empty temp dir has no toolchain of its own, so proceeding past the mismatch
        // lands on the ordinary "nothing to check" path rather than a Blocked result.
        Assert.True(result.Item is null || result.Item.State != WorkItemState.Blocked);
        Assert.Equal("no toolchain detected", result.Detail);
    }

    [Fact]
    public async Task NoRemediationAvailable_ReturnsBlocked()
    {
        using var host = FactoryHost.Init(_dir, transport: new FakeTransport());
        var item = WorkItem.Create("needs a newer toolchain");

        var runner = new FakeRemediationRunner(RemediationResult.NotFound);
        var mismatch = ToolchainCompatibilityResult.Incompatible(["9.0.100"], ["8.0.100"]);
        var station = new CheckStation(runner, new FakeToolchainProbe(mismatch));

        var result = await station.ExecuteAsync(ContextFor(host, item));

        Assert.Equal(1, runner.Calls);
        Assert.Equal(WorkItemState.Blocked, result.Item?.State);
        Assert.Contains("9.0.100", result.Detail);
        Assert.Contains("8.0.100", result.Detail);
    }

    [Fact]
    public async Task RemediationFailsOrRecheckMismatches_ReturnsBlocked()
    {
        using var host = FactoryHost.Init(_dir, transport: new FakeTransport());
        var item = WorkItem.Create("needs a newer toolchain");

        // The runner reports success, but the re-check is what is trusted — and it still
        // mismatches, so this must block rather than proceed.
        var runner = new FakeRemediationRunner(new RemediationResult(true, true, true, "installed", null));
        var mismatch = ToolchainCompatibilityResult.Incompatible(["9.0.100"], ["8.0.100"]);
        var station = new CheckStation(runner, new FakeToolchainProbe(mismatch, mismatch));

        var result = await station.ExecuteAsync(ContextFor(host, item));

        Assert.Equal(1, runner.Calls);
        Assert.True(result.Success);
        Assert.Equal(WorkItemState.Blocked, result.Item?.State);
        Assert.NotEqual(WorkItemState.Failed, result.Item?.State);
    }

    [Fact]
    public async Task ToolchainMismatch_IsNeverCapturedIntoTheBaseline()
    {
        using var host = FactoryHost.Init(_dir, transport: new FakeTransport());
        File.WriteAllText(Path.Combine(_dir, "App.csproj"), "<Project/>");

        var mismatch = ToolchainCompatibilityResult.Incompatible(["9.0.100"], ["8.0.100"]);
        var baseline = await CheckStation.CaptureBaselineAsync(
            host.Services, probe: new FakeToolchainProbe(mismatch));

        Assert.Null(baseline);
        Assert.False(File.Exists(host.Services.Paths.BaselineFile));
    }

    [Fact]
    public async Task GenuineFailure_IsStillCapturedIntoTheBaseline()
    {
        using var host = FactoryHost.Init(_dir, transport: new FakeTransport());
        // Broken XML so `dotnet build` fails fast without needing a restorable project.
        File.WriteAllText(Path.Combine(_dir, "App.csproj"), "not a real project");

        var compatible = ToolchainCompatibilityResult.Compatible();
        var baseline = await CheckStation.CaptureBaselineAsync(
            host.Services, probe: new FakeToolchainProbe(compatible));

        Assert.NotNull(baseline);
        Assert.False(baseline!.Passing["build"]);
        Assert.True(File.Exists(host.Services.Paths.BaselineFile));
    }
}
