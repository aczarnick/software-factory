using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

public sealed class ToolchainBaselineStalenessTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    private sealed class FakeRepoStateProvider(string sha) : IRepoStateProvider
    {
        public Task<string> GetCurrentMasterShaAsync(CancellationToken ct = default) => Task.FromResult(sha);

        // Baseline staleness does not consult harness staleness; these tests never reach it.
        public Task<int?> CommitsBehindHeadAsync(string commit, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);
    }

    private static Toolchain OneCheck() => new()
    {
        Name = "dotnet",
        Checks = [new ToolchainCheck("build", "dotnet build", 60)]
    };

    private string CachePath => Path.Combine(_dir, "baseline.json");

    [Fact]
    public async Task NewCommitInvalidatesCachedBaseline()
    {
        var cached = new ToolchainBaseline
        {
            Commit = "A",
            Passing = new Dictionary<string, bool> { ["build"] = false }
        };

        var probeCalls = 0;
        var baseline = await ToolchainRunner.GetOrRecaptureBaselineAsync(
            cached, OneCheck(), _dir, CachePath, new FakeRepoStateProvider("B"),
            execute: (_, _, _) =>
            {
                probeCalls++;
                return Task.FromResult(new ShellResult(0, "ok", "", false));
            });

        Assert.Equal(1, probeCalls);
        Assert.Equal("B", baseline.Commit);
        // The fresh result is what decides pass/fail, not the stale cached verdict.
        Assert.True(baseline.Passing["build"]);
    }

    [Fact]
    public async Task SameCommitReusesCachedBaseline()
    {
        var cached = new ToolchainBaseline
        {
            Commit = "A",
            Passing = new Dictionary<string, bool> { ["build"] = false }
        };

        var probeCalls = 0;
        var baseline = await ToolchainRunner.GetOrRecaptureBaselineAsync(
            cached, OneCheck(), _dir, CachePath, new FakeRepoStateProvider("A"),
            execute: (_, _, _) =>
            {
                probeCalls++;
                return Task.FromResult(new ShellResult(0, "ok", "", false));
            });

        Assert.Equal(0, probeCalls);
        Assert.Same(cached, baseline);
    }
}
