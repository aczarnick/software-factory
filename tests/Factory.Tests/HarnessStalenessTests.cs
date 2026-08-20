using Factory.Runtime;

namespace Factory.Tests;

/// <summary>The factory commits improvements to its own source, but the binary on PATH is whatever
/// was last installed. Nothing compared the two, so a harness could build with code it was not
/// running and report nothing about it.</summary>
public class HarnessStalenessTests
{
    private sealed class FakeRepo(string head, int? behind) : IRepoStateProvider
    {
        public Task<string> GetCurrentMasterShaAsync(CancellationToken ct = default) => Task.FromResult(head);

        public Task<int?> CommitsBehindHeadAsync(string sha, CancellationToken ct = default) =>
            Task.FromResult(behind);
    }

    [Fact]
    public async Task ABinaryBuiltFromHeadIsNotStale()
    {
        var report = await HarnessStaleness.ProbeAsync("abc123", new FakeRepo("abc123", behind: 0));

        Assert.True(report.SelfHosted);
        Assert.False(report.IsStale);
    }

    [Fact]
    public async Task ABinaryBehindHeadIsStaleAndCountsTheGap()
    {
        var report = await HarnessStaleness.ProbeAsync("old111", new FakeRepo("new222", behind: 135));

        Assert.True(report.IsStale);
        Assert.Equal(135, report.CommitsBehind);
    }

    [Fact]
    public async Task TheWarningNamesBothCommitsAndTheRemedy()
    {
        var report = await HarnessStaleness.ProbeAsync("old111", new FakeRepo("new222", behind: 135));

        Assert.Contains("old111", report.Describe);
        Assert.Contains("new222", report.Describe);
        Assert.Contains("135", report.Describe);
        Assert.Contains("install.sh", report.Describe);
    }

    [Fact]
    public async Task ARepositoryThatDoesNotContainTheBuildCommitIsNotSelfHosted()
    {
        // Building some other project. The harness is not this repository's own output, so its
        // version says nothing about the code being built and must not raise a warning.
        var report = await HarnessStaleness.ProbeAsync("abc123", new FakeRepo("zzz999", behind: null));

        Assert.False(report.SelfHosted);
        Assert.False(report.IsStale);
    }

    [Fact]
    public async Task ABuildWithNoRecordedCommitIsNotStale()
    {
        // Built outside a git checkout: there is nothing to compare, and a warning would be noise.
        var report = await HarnessStaleness.ProbeAsync(null, new FakeRepo("new222", behind: 135));

        Assert.False(report.SelfHosted);
        Assert.False(report.IsStale);
    }
}
