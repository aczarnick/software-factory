using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

/// <summary>
/// One throwaway beads database per test class: `bd init` costs about four seconds, so the cost is
/// paid once rather than per test. Every test therefore works only on beads it filed itself, and
/// the claim tests drain the ready queue first so their result cannot depend on execution order.
/// The database lives under a temp directory — the repository's own .beads and .factory are never
/// touched.
/// </summary>
public sealed class BeadsDatabase : IDisposable
{
    public string Directory { get; } = TempDir.Create();
    public bool Available { get; } = Shell.Which("bd");

    public BeadsDatabase()
    {
        if (!Available) return;

        Shell.Run("git", ["init", "-q", "."], Directory);
        var cli = new BeadsCli(Directory);
        cli.Exec("init", "--init-if-missing", "--prefix", "wi");
        cli.Exec("config", "set", "status.custom", BeadMapper.CustomStatuses);
        cli.Exec("config", "set", "types.custom", BeadMapper.CustomTypes);
    }

    public void Dispose() => TempDir.Delete(Directory);
}

public class BeadsWorkItemStoreTests(BeadsDatabase database) : IClassFixture<BeadsDatabase>
{
    private const string Owner = "test-machine";

    private BeadsCli Cli() => new(database.Directory);
    private BeadsWorkItemStore Store() => new(Cli(), Owner);

    // bd is on PATH in this environment, so these run for real. A machine without bd skips them
    // rather than failing: the rest of the suite must stay offline-clean.
    private bool Unavailable => !database.Available;

    private BeadRecord Bead(string id) =>
        Cli().Json<BeadRecord>([.. BeadMapper.GetArgs(id)]).Single();

    /// <summary>Empties the ready queue so a claim test sees only what it filed.</summary>
    private void DrainReadyQueue()
    {
        var store = Store();
        while (store.TryClaim(Owner) is { } claimed)
            store.Update(claimed with { State = WorkItemState.Cancelled });
    }

    [Fact]
    public void Add_then_Get_round_trips_a_work_item()
    {
        if (Unavailable) return;
        var store = Store();

        var item = store.Add(WorkItem.Create("build a thing", "because") with
        {
            State = WorkItemState.Ready,
            Priority = 1,
            AcceptanceCriteria = [AcceptanceCriterion.Command("runs", "dotnet run")]
        });

        var restored = store.Get(item.Id)!;

        Assert.Equal("build a thing", restored.Title);
        Assert.Equal("because", restored.Intent);
        Assert.Equal(WorkItemState.Ready, restored.State);
        Assert.Equal(1, restored.Priority);
        Assert.Single(restored.AcceptanceCriteria);
        Assert.IsType<CommandVerification>(restored.AcceptanceCriteria[0].Verification);
    }

    [Fact]
    public void Add_files_a_proposal_as_a_draft_rather_than_as_ready_work()
    {
        if (Unavailable) return;
        var store = Store();

        var item = store.Add(WorkItem.Create("a proposal"));

        Assert.Equal(WorkItemState.Draft, store.Get(item.Id)!.State);
    }

    [Fact]
    public void Add_then_Get_round_trips_dependencies()
    {
        if (Unavailable) return;
        var store = Store();
        var blocker = store.Add(WorkItem.Create("blocker") with { State = WorkItemState.Ready });

        var dependent = store.Add(WorkItem.Create("dependent") with
        {
            State = WorkItemState.Ready,
            DependsOn = [blocker.Id]
        });

        Assert.Equal([blocker.Id], store.Get(dependent.Id)!.DependsOn);
    }

    [Fact]
    public void Get_returns_null_for_an_id_the_backlog_does_not_know()
    {
        if (Unavailable) return;

        Assert.Null(Store().Get("wi-000000000000"));
    }

    [Fact]
    public void TryClaim_marks_the_item_in_progress_and_assigns_it_to_the_named_owner()
    {
        if (Unavailable) return;
        DrainReadyQueue();
        var store = Store();
        store.Add(WorkItem.Create("claimable") with { State = WorkItemState.Ready });

        var claimed = store.TryClaim(Owner)!;

        Assert.Equal(WorkItemState.InProgress, store.Get(claimed.Id)!.State);

        // The assignee identifies the checkout, not whoever's git identity bd would have guessed.
        Assert.Equal(Owner, Bead(claimed.Id).Assignee);
    }

    [Fact]
    public void TryClaim_takes_a_lease_the_factory_can_refresh()
    {
        if (Unavailable) return;
        DrainReadyQueue();
        var store = Store();
        store.Add(WorkItem.Create("leased") with { State = WorkItemState.Ready });

        var claimed = store.TryClaim(Owner)!;
        var granted = Bead(claimed.Id).LeaseExpiresAt;

        Assert.NotNull(granted);

        store.Heartbeat(claimed.Id);

        Assert.True(Bead(claimed.Id).LeaseExpiresAt >= granted,
            "a heartbeat must not shorten the lease it refreshes");
    }

    [Fact]
    public void TryClaim_withholds_an_item_with_an_unmet_dependency()
    {
        if (Unavailable) return;
        DrainReadyQueue();
        var store = Store();
        var blocker = store.Add(WorkItem.Create("first") with { State = WorkItemState.Ready });
        store.Add(WorkItem.Create("second") with
        {
            State = WorkItemState.Ready,
            DependsOn = [blocker.Id]
        });

        // The blocker is claimable; the item waiting on it is not, until the blocker closes.
        Assert.Equal(blocker.Id, store.TryClaim(Owner)!.Id);
        Assert.Null(store.TryClaim(Owner));
    }

    [Fact]
    public void A_draft_item_is_never_claimed()
    {
        if (Unavailable) return;
        DrainReadyQueue();
        var store = Store();

        store.Add(WorkItem.Create("proposal"));

        Assert.Null(store.TryClaim(Owner));
    }

    [Fact]
    public void Release_returns_a_claimed_item_to_the_queue_and_drops_its_lease()
    {
        if (Unavailable) return;
        DrainReadyQueue();
        var store = Store();
        store.Add(WorkItem.Create("to be released") with { State = WorkItemState.Ready });
        var claimed = store.TryClaim(Owner)!;

        store.Release(claimed.Id, "requeued after restart");

        var bead = Bead(claimed.Id);
        Assert.Equal(WorkItemState.Ready, BeadMapper.StateFor(bead.Status));

        // A requeued orphan that kept its lease or its assignee could not be taken by another machine.
        Assert.True(string.IsNullOrEmpty(bead.Assignee), $"assignee should be cleared, was '{bead.Assignee}'");
        Assert.Null(bead.LeaseExpiresAt);
    }

    [Fact]
    public void Release_requeues_an_item_that_a_station_has_already_moved_past_in_progress()
    {
        if (Unavailable) return;
        DrainReadyQueue();
        var store = Store();
        store.Add(WorkItem.Create("mid pipeline") with { State = WorkItemState.Ready });
        var claimed = store.TryClaim(Owner)!;
        var reviewing = store.Transition(claimed, WorkItemState.InReview, "handed to review");

        store.Release(reviewing.Id, "requeued after restart");

        Assert.Equal(WorkItemState.Ready, store.Get(reviewing.Id)!.State);
    }

    [Fact]
    public void Release_does_nothing_for_an_id_the_backlog_does_not_know()
    {
        if (Unavailable) return;

        Store().Release("wi-000000000000", "requeued after restart");
    }

    [Fact]
    public void Heartbeat_does_not_throw_for_an_item_this_node_does_not_hold()
    {
        if (Unavailable) return;
        var store = Store();
        var item = store.Add(WorkItem.Create("unheld") with { State = WorkItemState.Ready });

        // bd refuses a heartbeat on a bead that is not in progress here; that is not a backlog failure.
        store.Heartbeat(item.Id);
    }

    [Fact]
    public void Transition_refuses_a_move_the_state_machine_does_not_allow()
    {
        if (Unavailable) return;
        var store = Store();
        var item = store.Add(WorkItem.Create("fresh") with { State = WorkItemState.Ready });

        Assert.Throws<InvalidOperationException>(() =>
            store.Transition(item, WorkItemState.Verified, "skipping the pipeline"));
    }

    [Fact]
    public void All_reports_work_in_every_status_including_closed_and_draft()
    {
        if (Unavailable) return;
        var store = Store();
        var draft = store.Add(WorkItem.Create("a draft"));
        var ready = store.Add(WorkItem.Create("ready work") with { State = WorkItemState.Ready });
        var done = store.Add(WorkItem.Create("finished") with { State = WorkItemState.Done });

        var ids = store.All().Select(i => i.Id).ToList();

        Assert.Contains(draft.Id, ids);
        Assert.Contains(ready.Id, ids);
        Assert.Contains(done.Id, ids);
    }

    [Fact]
    public void Reclaim_reverts_nothing_while_every_lease_is_live()
    {
        if (Unavailable) return;

        // The live behaviour needs the 5-minute lease to expire, so it is evidenced by probe and by
        // BeadsArgumentTests rather than by waiting here. What this pins is that a reclaim pass over
        // healthy leases is a no-op that neither throws nor invents items.
        Assert.Empty(Store().Reclaim(TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void Sync_without_a_remote_does_not_throw()
    {
        if (Unavailable) return;

        Store().Sync();
    }
}
