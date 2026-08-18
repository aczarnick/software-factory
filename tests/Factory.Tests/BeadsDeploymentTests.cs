using Factory.Runtime;

namespace Factory.Tests;

public class BeadsDeploymentTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    private static bool Available => Shell.Which("bd");

    public BeadsDeploymentTests() => Shell.Run("git", ["init", "-q", "."], _dir);
    public void Dispose() => TempDir.Delete(_dir);

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
