using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

/// <summary>
/// The whole integration, against a real beads database in a temp directory: selecting the provider
/// by config, deploying it, holding a claim open across a run, and requeueing an orphan.
/// </summary>
public class BeadsBackedFactoryTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    private static bool Available => Shell.Which("bd");

    public BeadsBackedFactoryTests() => Shell.Run("git", ["init", "-q", "."], _dir);
    public void Dispose() => TempDir.Delete(_dir);

    private const string SingleChild =
        """{"children":[{"key":"a","title":"do it","kind":"Feature","requirements":["works"],"acceptanceCriteria":[]}]}""";

    private const string Plan =
        """{"files":[{"path":"hello.txt","change":"create"}],"steps":["write the file"],"risks":[]}""";

    private static FakeTransport Scripted() =>
        new FakeTransport()
            .Respond("decompose", SingleChild)
            .Respond("plan", Plan)
            .Respond("implement", request =>
            {
                File.WriteAllText(Path.Combine(request.WorkingDirectory!, "hello.txt"), "hi\n");
                return FakeTransport.Success("wrote the file", cost: 0.02m);
            });

    private FactoryHost OpenBeadsBacked(FakeTransport transport) =>
        FactoryHost.Init(_dir, config: new FactoryConfig
        {
            Name = "test-machine",
            BlueprintName = Blueprint.Standard().Name,
            MaxConcurrency = 1,
            WorkItemStore = new ProviderRef("beads")
        }, transport: transport);

    private BeadRecord Bead(string id) =>
        new BeadsCli(_dir).Json<BeadRecord>([.. BeadMapper.GetArgs(id)]).Single();

    private BeadRecord? InProgressBead() =>
        new BeadsCli(_dir)
            .Json<BeadRecord>("list", "--status", "in_progress", "--limit", "0", "--json")
            .FirstOrDefault();

    [Fact]
    public void Selecting_the_provider_by_config_files_work_into_beads()
    {
        if (!Available) return;
        using var host = OpenBeadsBacked(Scripted());

        var item = host.Submit(WorkItem.Create("prove the beads backlog works"));

        // Authoritative means the item is really in beads, not only in the ledger fold.
        Assert.Equal("prove the beads backlog works", Bead(item.Id).Title);
        Assert.Contains(item.Id, host.Services.State.Items.Keys);
    }

    [Fact]
    public async Task A_claim_is_refreshed_while_its_station_works()
    {
        if (!Available) return;

        // Observed from inside the run, while the item is still in progress: once it finishes it is
        // closed and has no lease left, so anything asserted afterwards would pass either way.
        BeadRecord? observed = null;

        var transport = new FakeTransport()
            .Respond("decompose", SingleChild)
            .Respond("plan", Plan)
            .Respond("implement", request =>
            {
                Thread.Sleep(300);
                observed = InProgressBead();
                File.WriteAllText(Path.Combine(request.WorkingDirectory!, "hello.txt"), "hi\n");
                return FakeTransport.Success("wrote the file", cost: 0.02m);
            });

        using var host = OpenBeadsBacked(transport);
        host.Submit(WorkItem.Create("held across a run"));

        await host.CreateOrchestrator().RunAsync(new OrchestratorOptions
        {
            StopWhenIdle = true,
            MaxItems = 1,
            // The real cadence is minutes, so a run measured in milliseconds needs it lowered for
            // a refresh to fall inside the window this observes.
            LeaseRefreshInterval = TimeSpan.FromMilliseconds(40)
        });

        Assert.NotNull(observed);
        Assert.NotNull(observed!.HeartbeatAt);
        Assert.NotNull(observed.StartedAt);

        // With MaxItems of 1 the claim loop exits immediately after claiming, so the whole station
        // run happens in the drain that follows it. A refresh driven from the poll loop — which is
        // what the plan specified — would never fire here at all.
        Assert.True(observed.HeartbeatAt > observed.StartedAt,
            $"the claim should have been refreshed past its grant: started {observed.StartedAt:O}, " +
            $"last heartbeat {observed.HeartbeatAt:O}");
    }

    [Fact]
    public async Task An_orphan_is_requeued_with_its_claim_dropped_so_another_machine_can_take_it()
    {
        if (!Available) return;
        using var host = OpenBeadsBacked(Scripted());
        var store = host.Services.Items;

        // Stand in for a crash: claimed, in progress, lease held, then the process died.
        store.Add(WorkItem.Create("interrupted") with { State = WorkItemState.Ready });
        var claimed = store.TryClaim("test-machine")!;
        Assert.Equal("test-machine", Bead(claimed.Id).Assignee);

        await host.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true, MaxItems = 0 });

        var bead = Bead(claimed.Id);
        Assert.Equal(WorkItemState.Ready, BeadMapper.StateFor(bead.Status));
        Assert.True(string.IsNullOrEmpty(bead.Assignee),
            $"a requeued orphan that keeps its assignee cannot be taken by another machine, was '{bead.Assignee}'");
        Assert.Null(bead.LeaseExpiresAt);
    }
}
