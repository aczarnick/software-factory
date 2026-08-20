using Factory.Runtime;

namespace Factory.Tests;

public sealed class BeadsDeploymentTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    private static bool Available => Shell.Which("bd");

    public BeadsDeploymentTests() => Shell.Run("git", ["init", "-q", "."], _dir);
    public void Dispose() => TempDir.Delete(_dir);

    /// <summary>Reports <c>bd</c> as missing while every other call goes to the real executable, so
    /// a test can tell whether the guard refused before reaching one. <see cref="Shell.Which"/> reads
    /// the login shell's PATH, which a test process cannot take <c>bd</c> out of; this is the only
    /// way the guard is reachable on a machine that has it.</summary>
    private sealed class ReportsBeadsMissing(string directory, string owner) : BeadsCli(directory, owner)
    {
        public override bool IsAvailable => false;
    }

    [Fact]
    public void Deploying_without_bd_installed_names_both_ways_out_and_writes_nothing()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            BeadsDeployment.EnsureInitialised(new ReportsBeadsMissing(_dir, "test-machine"), "wi", _ => { }));

        // The first failure a new operator meets, so it has to name the install and the config escape
        // rather than surfacing as whatever `bd init` prints when the binary is not there.
        Assert.Contains("bd", error.Message);
        Assert.Contains("\"provider\": \"ledger\"", error.Message);

        // And it has to refuse before the first write, not after: bd really is on PATH here, so a
        // guard that only reported would still leave a half-made beads database behind in a
        // deployment that has to fall back to the ledger.
        Assert.False(Directory.Exists(Path.Combine(_dir, ".beads")),
            "the guard must refuse before bd init, leaving nothing behind to clean up");
    }

    [Fact]
    public void Deploying_installs_the_vocabulary_the_mapping_needs()
    {
        if (!Available) return;
        var cli = new BeadsCli(_dir, "test-machine");

        BeadsDeployment.EnsureInitialised(cli, "wi", _ => { });

        var statuses = cli.Exec("statuses");
        Assert.True(statuses.Ok, statuses.Combined);
        foreach (var status in new[] { "draft", "in_review", "verified", "failed", "cancelled" })
            Assert.Contains(status, statuses.Stdout);
    }

    [Fact]
    public void Deploying_twice_is_not_an_error()
    {
        if (!Available) return;
        var cli = new BeadsCli(_dir, "test-machine");

        BeadsDeployment.EnsureInitialised(cli, "wi", _ => { });

        // Every open runs this, and bd init refuses a second init without --init-if-missing.
        BeadsDeployment.EnsureInitialised(cli, "wi", _ => { });
    }

    [Fact]
    public void Deploying_keeps_work_that_is_already_filed()
    {
        if (!Available) return;
        var cli = new BeadsCli(_dir, "test-machine");
        BeadsDeployment.EnsureInitialised(cli, "wi", _ => { });
        cli.Exec("create", "existing work", "--id", "wi-aaaa11112222", "--json");

        BeadsDeployment.EnsureInitialised(cli, "wi", _ => { });

        Assert.NotNull(new BeadsWorkItemStore(cli, "test-machine", _ => { }).Get("wi-aaaa11112222"));
    }
}
