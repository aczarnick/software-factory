using Factory.Runtime;

namespace Factory.Tests;

public class DotnetToolchainProbeTests
{
    private sealed class FakeToolchainRequirementReader(RepoToolchainRequirement requirement) : IToolchainRequirementReader
    {
        public Task<RepoToolchainRequirement> ReadRequirementsAsync(string repoPath, CancellationToken ct = default) =>
            Task.FromResult(requirement);
    }

    private sealed class FakeInstalledSdkProvider(IReadOnlyList<string> versions) : IInstalledSdkProvider
    {
        public Task<IReadOnlyList<string>> GetInstalledVersionsAsync(CancellationToken ct = default) =>
            Task.FromResult(versions);
    }

    [Fact]
    public async Task ProbeAsyncInstalledPatchSatisfiesRollForwardReturnsCompatible()
    {
        var reader = new FakeToolchainRequirementReader(new RepoToolchainRequirement(["6.0.100"], []));
        var provider = new FakeInstalledSdkProvider(["6.0.108"]);
        var probe = new DotnetToolchainProbe(reader, provider);

        var result = await probe.ProbeAsync("/repo");

        Assert.True(result.IsCompatible);
        Assert.Empty(result.RequiredVersions);
        Assert.Empty(result.InstalledVersions);
    }

    [Fact]
    public async Task ProbeAsyncInstalledMajorVersionDiffersReturnsMismatchWithExactVersionStrings()
    {
        var reader = new FakeToolchainRequirementReader(new RepoToolchainRequirement(["6.0.100"], []));
        var provider = new FakeInstalledSdkProvider(["7.0.101"]);
        var probe = new DotnetToolchainProbe(reader, provider);

        var result = await probe.ProbeAsync("/repo");

        Assert.False(result.IsCompatible);
        var mismatch = new ToolchainMismatch(result.RequiredVersions, result.InstalledVersions);
        Assert.Equal(["6.0.100"], mismatch.RequiredVersions);
        Assert.Equal(["7.0.101"], mismatch.InstalledVersions);
    }

    [Fact]
    public async Task ProbeAsyncInstalledFeatureBandDiffersReturnsMismatch()
    {
        // Same major and minor as required, but a different feature band (200 vs 100) — a
        // naive "same major" comparer would wrongly call this compatible.
        var reader = new FakeToolchainRequirementReader(new RepoToolchainRequirement(["6.0.100"], []));
        var provider = new FakeInstalledSdkProvider(["6.0.200"]);
        var probe = new DotnetToolchainProbe(reader, provider);

        var result = await probe.ProbeAsync("/repo");

        Assert.False(result.IsCompatible);
        var mismatch = new ToolchainMismatch(result.RequiredVersions, result.InstalledVersions);
        Assert.Equal(["6.0.100"], mismatch.RequiredVersions);
        Assert.Equal(["6.0.200"], mismatch.InstalledVersions);
    }
}
