using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

/// <summary>
/// The whole integration, against a real beads database in a temp directory: selecting the provider
/// by config, deploying it, holding a claim open across a run, requeueing an orphan, and returning
/// work to a queue it can actually be claimed from again.
///
/// A deployment per test rather than a shared fixture: each test opens its own factory, and a
/// shared ledger would let one test's Ready item answer another's claim.
/// </summary>
public class BeadsBackedFactoryTests : IDisposable
{
    private const string Owner = "test-machine";

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
            Name = Owner,
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
        var claimed = store.TryClaim(Owner)!;
        Assert.Equal(Owner, Bead(claimed.Id).Assignee);

        await host.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true, MaxItems = 0 });

        var bead = Bead(claimed.Id);
        Assert.Equal(WorkItemState.Ready, BeadMapper.StateFor(bead.Status));
        Assert.True(string.IsNullOrEmpty(bead.Assignee),
            $"a requeued orphan that keeps its assignee cannot be taken by another machine, was '{bead.Assignee}'");
        Assert.Null(bead.LeaseExpiresAt);
    }

    [Fact]
    public void Reopening_an_unchanged_backlog_reports_no_corrections()
    {
        if (!Available) return;

        using (var host = OpenBeadsBacked(Scripted()))
            host.Submit(WorkItem.Create("filed here", "and unchanged since"));

        var log = new List<string>();
        using var reopened = FactoryHost.Open(_dir, log.Add, transport: Scripted());

        // An item filed by this checkout is already in the ledger, so a reopen has nothing to
        // correct. If it corrects anyway, every open rewrites the whole backlog into the ledger
        // for as long as the factory lives.
        Assert.DoesNotContain(log, message => message.Contains("reconciled"));
    }

    // Every path that puts work back on the queue asserts the item is claimable again rather than
    // that its status is Ready. bd's `ready --claim` skips an open bead that still carries an
    // assignee — even for the actor named in it — so an item returned to Ready with its claim
    // intact is Ready everywhere and claimable nowhere, and a status assertion sees nothing wrong.

    [Fact]
    public void Activating_a_blocked_item_leaves_it_claimable_again()
    {
        if (!Available) return;
        using var host = OpenBeadsBacked(new FakeTransport());
        var store = host.Services.Items;

        store.Add(WorkItem.Create("blocked on a person") with { State = WorkItemState.Ready });
        var blocked = host.Transition(store.TryClaim(Owner)!, WorkItemState.Blocked, "needs a decision");

        host.Activate(blocked);

        Assert.Equal(blocked.Id, store.TryClaim(Owner)?.Id);
    }

    [Fact]
    public void Retrying_a_failed_item_leaves_it_claimable_again()
    {
        if (!Available) return;
        using var host = OpenBeadsBacked(new FakeTransport());
        var store = host.Services.Items;

        store.Add(WorkItem.Create("failed on something outside itself") with { State = WorkItemState.Ready });
        var failed = host.Transition(store.TryClaim(Owner)!, WorkItemState.Failed, "verify gate failed");

        host.Activate(failed);

        Assert.Equal(failed.Id, store.TryClaim(Owner)?.Id);
    }

    [Fact]
    public async Task An_item_a_cancelled_run_returns_to_the_queue_is_claimable_again()
    {
        if (!Available) return;

        using var cancellation = new CancellationTokenSource();

        // Ctrl-C while the first station is working. The next station's cancellation check unwinds
        // the run through the orchestrator's handler, which returns the item to Ready.
        var transport = new FakeTransport().Respond("decompose", _ =>
        {
            cancellation.Cancel();
            return FakeTransport.Success(SingleChild);
        });

        using var host = OpenBeadsBacked(transport);
        var item = host.Submit(WorkItem.Create("interrupted mid-run"));

        await host.CreateOrchestrator().RunAsync(
            new OrchestratorOptions { StopWhenIdle = true, MaxItems = 1 }, cancellation.Token);

        Assert.Equal(WorkItemState.Ready, host.Services.Items.Get(item.Id)!.State);
        Assert.Equal(item.Id, host.Services.Items.TryClaim(Owner)?.Id);
    }

    [Fact]
    public async Task Requeueing_orphans_leaves_an_item_another_checkout_holds_in_progress_alone()
    {
        if (!Available) return;

        const string elsewhere = "other-machine";
        string foreignId;
        string mineId;

        using (var host = OpenBeadsBacked(new FakeTransport()))
        {
            var store = host.Services.Items;

            store.Add(WorkItem.Create("another checkout's work") with { State = WorkItemState.Ready });
            foreignId = new BeadsWorkItemStore(new BeadsCli(_dir), elsewhere).TryClaim(elsewhere)!.Id;

            store.Add(WorkItem.Create("my interrupted work") with { State = WorkItemState.Ready });
            mineId = store.TryClaim(Owner)!.Id;
        }

        // Reopening folds the foreign claim in from the shared backlog, which is how a claim this
        // checkout never made becomes visible to its orphan requeue in the first place.
        var log = new List<string>();
        using var reopened = FactoryHost.Open(_dir, log.Add, transport: new FakeTransport());
        Assert.Equal(elsewhere, reopened.Services.State.Items[foreignId].Owner);

        // bd refuses to clear the assignee of a bead another actor holds in progress, so requeueing
        // it would throw and take the whole run start down with it.
        await reopened.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true, MaxItems = 0 });

        var foreignBead = Bead(foreignId);
        Assert.Equal(WorkItemState.InProgress, BeadMapper.StateFor(foreignBead.Status));
        Assert.Equal(elsewhere, foreignBead.Assignee);
        Assert.Contains(log, message => message.Contains(foreignId) && message.Contains(elsewhere));

        // Reported, not reaped — and this checkout's own orphan is still requeued and claimable.
        Assert.Equal(mineId, reopened.Services.Items.TryClaim(Owner)?.Id);
    }
}
