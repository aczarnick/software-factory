using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

/// <summary>A baseline that records a check as already-failing switches that gate off: only
/// regressions block, and a check believed broken can no longer regress. That is a big thing to do
/// silently on evidence the capture then throws away.</summary>
public class BaselineConfidenceTests
{
    private static CheckOutcome Outcome(string name, bool passed, int attempts) =>
        new(name, passed, passed ? "passed" : "failed", 10, attempts);

    [Fact]
    public void ABaselineKeepsHowManyAttemptsEachCheckNeeded()
    {
        var baseline = ToolchainBaseline.From("sha1", [Outcome("build", true, 1), Outcome("test", false, 2)]);

        Assert.Equal(1, baseline.Attempts["build"]);
        Assert.Equal(2, baseline.Attempts["test"]);
    }

    [Fact]
    public void ACheckRecordedAsFailingIsNamedAsAGateThatNoLongerBlocks()
    {
        var baseline = ToolchainBaseline.From("sha1", [Outcome("build", true, 1), Outcome("test", false, 2)]);

        Assert.Equal(["test"], baseline.DisabledGates);
    }

    [Fact]
    public void AFullyPassingBaselineDisablesNothing()
    {
        var baseline = ToolchainBaseline.From("sha1", [Outcome("build", true, 1), Outcome("test", true, 1)]);

        Assert.Empty(baseline.DisabledGates);
    }

    [Fact]
    public void ACheckThatPassedOnlyOnRetryIsReportedAsFlaky()
    {
        // Passing on the second attempt means the machine, not the code, decided the first one.
        // The gate stays on, but the evidence that the host is unreliable must not be discarded.
        var baseline = ToolchainBaseline.From("sha1", [Outcome("build", true, 2)]);

        Assert.Equal(["build"], baseline.FlakyChecks);
        Assert.Empty(baseline.DisabledGates);
    }

    [Fact]
    public void ABaselineWrittenBeforeAttemptsWereRecordedStillLoads()
    {
        // Old cache files on disk predate the Attempts map; a JSON failure here would discard a
        // usable baseline and force a recapture at the worst possible moment.
        const string legacy = """
            {"commit":"sha1","passing":{"build":true,"test":false},"capturedAt":"2026-08-19T13:57:53+00:00"}
            """;

        var baseline = FactoryJson.Read<ToolchainBaseline>(legacy);

        Assert.NotNull(baseline);
        Assert.Equal(["test"], baseline!.DisabledGates);
        Assert.Empty(baseline.Attempts);
    }
}
