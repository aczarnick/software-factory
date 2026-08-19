using System.Text.Json.Serialization;
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
        var cli = new BeadsCli(Directory, "test-machine");
        cli.Exec("init", "--init-if-missing", "--prefix", "wi");
        cli.Exec("config", "set", "status.custom", BeadMapper.CustomStatuses);
        cli.Exec("config", "set", "types.custom", BeadMapper.CustomTypes);
    }

    public void Dispose() => TempDir.Delete(Directory);
}

public class BeadsWorkItemStoreTests(BeadsDatabase database) : IClassFixture<BeadsDatabase>
{
    private const string Owner = "test-machine";

    private BeadsCli Cli() => new(database.Directory, Owner);
    private BeadsWorkItemStore Store() => new(Cli(), Owner, _ => { });

    // bd is on PATH in this environment, so these run for real. A machine without bd skips them
    // rather than failing: the rest of the suite must stay offline-clean.
    private bool Unavailable => !database.Available;

    private BeadRecord Bead(string id) =>
        Cli().Json<BeadRecord>([.. BeadMapper.GetArgs(id)]).Single();

    /// <summary>Lets a test act on the bead in the window between <c>Add</c>'s two writes. That
    /// window is bounded by a single <c>bd</c> process start, so nothing outside this seam can reach
    /// it deterministically — and both of its outcomes, a lost race and a genuine failure, only
    /// happen there.</summary>
    private sealed class InterposesAfterCreate : BeadsCli
    {
        private readonly Action<string> _afterCreate;

        public InterposesAfterCreate(string directory, string owner, Action<string> afterCreate)
            : base(directory, owner) => _afterCreate = afterCreate;

        public override ShellResult Exec(params string[] args)
        {
            var result = base.Exec(args);
            if (result.Ok && args is ["create", ..]) _afterCreate(IdIn(args));

            return result;
        }

        private static string IdIn(string[] args) => args[Array.IndexOf(args, "--id") + 1];
    }

    /// <summary>Empties the ready queue so a claim test sees only what it filed.</summary>
    private void DrainReadyQueue()
    {
        var store = Store();
        while (store.TryClaim(Owner) is { } claimed)
            store.Update(claimed with { State = WorkItemState.Cancelled });
    }

    /// <summary>A ledger and fold of their own under a temp directory, so a reconcile in one test
    /// cannot see another's corrections. <c>Open</c> is what the orchestrator does at run start.</summary>
    private sealed class Fold : IDisposable
    {
        private readonly JsonlRunHistory _history =
            new(Path.Combine(TempDir.Create(), "ledger.jsonl"));

        public Fold() => State = _history.Replay();

        public FactoryState State { get; }

        public void Open(IWorkItemStore store) =>
            BacklogReconciler.Reconcile(store, State, _history, _ => { });

        public void Dispose() => _history.Dispose();
    }

    private const string ForeignType = "epic";
    private const string ForeignCriterion = "- a criterion a human wrote";

    /// <summary>Files a bead the way another tool would — raw <c>bd</c>, no factory metadata, a type
    /// the factory has no <see cref="WorkItemKind"/> for, and acceptance criteria in beads' own cell —
    /// then puts it through the path the orchestrator takes: claim the ready bead, write it back.</summary>
    private string AForeignBeadTheFactoryHasClaimedAndUpdated(string title)
    {
        DrainReadyQueue();

        var id = Ids.New("wi");
        var filed = Cli().Exec("create", title, "--id", id, "-t", ForeignType,
                               "-d", "a human wrote this", "--acceptance", ForeignCriterion, "--json");
        Assert.True(filed.Ok, filed.Combined);

        var store = Store();
        var claimed = store.TryClaim(Owner);
        Assert.Equal(id, claimed?.Id);
        store.Update(claimed!);

        return id;
    }

    [Fact]
    public void Updating_a_bead_another_tool_filed_keeps_the_acceptance_criteria_it_wrote()
    {
        if (Unavailable) return;

        var id = AForeignBeadTheFactoryHasClaimedAndUpdated("an epic a human filed");

        // Asserted from bd's own output, because the mapper's projection is what is under suspicion.
        // ToWorkItem reads criteria only from the factory's metadata blob, so a foreign bead's arrive
        // empty — and writing that emptiness back would erase beads' own cell for good.
        Assert.Equal(ForeignCriterion, Bead(id).AcceptanceCriteria);
    }

    [Fact]
    public void Updating_a_bead_another_tool_filed_keeps_the_type_it_was_given()
    {
        if (Unavailable) return;

        var id = AForeignBeadTheFactoryHasClaimedAndUpdated("a milestone a human filed");

        // Asserted from bd's own output for the same reason as the criteria above: KindFor is what is
        // under suspicion. An epic flattened to WorkItemKind.Feature is written straight back out as
        // `feature`, so a type the factory cannot name is a type it destroys on first touch.
        Assert.Equal(ForeignType, Bead(id).IssueType);
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
    public void Add_leaves_a_bead_another_checkout_claimed_mid_filing_alone()
    {
        if (Unavailable) return;
        const string elsewhere = "other-machine";
        var logged = new List<string>();
        var item = WorkItem.Create("filed while another checkout was claiming");
        var otherCheckout = new BeadsCli(database.Directory, elsewhere);
        var store = new BeadsWorkItemStore(
            new InterposesAfterCreate(database.Directory, Owner, id =>
            {
                var claim = otherCheckout.Exec("update", id, "--claim", "--actor", elsewhere);
                Assert.True(claim.Ok, claim.Combined);
            }),
            Owner,
            logged.Add);

        var filed = store.Add(item);

        // The other checkout is running it under a live lease, so forcing draft here would drop that
        // lease and cancel work already in flight elsewhere.
        var bead = Bead(item.Id);
        Assert.Equal("in_progress", bead.Status);
        Assert.Equal(elsewhere, bead.Assignee);
        Assert.NotNull(bead.LeaseExpiresAt);

        // And the item handed back has to be what beads says, not the Draft this checkout intended:
        // the fold is written from this return value, so anything else makes the fold lie.
        Assert.Equal(WorkItemState.InProgress, filed.State);
        Assert.Equal(elsewhere, filed.Owner);
        Assert.Contains(logged, message => message.Contains(item.Id));
    }

    [Fact]
    public void Add_still_fails_loudly_when_the_second_write_fails_for_any_other_reason()
    {
        if (Unavailable) return;
        var cli = Cli();
        var store = new BeadsWorkItemStore(
            new InterposesAfterCreate(database.Directory, Owner, id =>
            {
                var deleted = cli.Exec("delete", id, "--force");
                Assert.True(deleted.Ok, deleted.Combined);
            }),
            Owner,
            _ => { });

        // A bead deleted in the window fails the second write with exit 1, not the 13 that means a
        // precondition went stale. A guard that read every failure as a lost race would report a
        // broken backlog as a filed item.
        Assert.Throws<InvalidOperationException>(() => store.Add(WorkItem.Create("deleted mid-filing")));
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
    public void TryClaim_stamps_the_lease_with_this_checkouts_node_id()
    {
        if (Unavailable) return;
        DrainReadyQueue();
        var store = Store();
        store.Add(WorkItem.Create("claimable, cross-replica guard") with { State = WorkItemState.Ready });

        var claimed = store.TryClaim(Owner)!;

        // BEADS_NODE_ID has to reach bd at claim time, not only at reclaim time: bd stamps the
        // granting node onto the lease when it is taken, and a later reclaim on another replica
        // skips only a lease whose granting node differs from its own. A lease with no granting
        // node recorded is an unguarded lease, so this is the post-condition Reclaim depends on.
        Assert.Equal(Owner, Bead(claimed.Id).LeaseGrantedNode);
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
    public void Transitioning_a_claimed_item_back_to_ready_leaves_it_claimable_again()
    {
        if (Unavailable) return;
        DrainReadyQueue();
        var store = Store();
        store.Add(WorkItem.Create("returned to the queue") with { State = WorkItemState.Ready });
        var claimed = store.TryClaim(Owner)!;

        store.Transition(claimed, WorkItemState.Ready, "cancelled");

        // Claimable, not merely Ready: bd skips an open bead that still carries an assignee, even
        // for the actor named in it, so a status assertion here would pass over a stranded item.
        Assert.Equal(claimed.Id, store.TryClaim(Owner)?.Id);
    }

    [Fact]
    public void Transitioning_a_claimed_item_back_to_ready_reports_nobody_holding_it()
    {
        if (Unavailable) return;
        DrainReadyQueue();
        var store = Store();
        store.Add(WorkItem.Create("released by transition") with { State = WorkItemState.Ready });
        var claimed = store.TryClaim(Owner)!;
        Assert.Equal(Owner, store.Get(claimed.Id)!.Owner);

        store.Transition(claimed, WorkItemState.Ready, "cancelled");

        Assert.Null(store.Get(claimed.Id)!.Owner);
    }

    [Fact]
    public void Release_refuses_to_put_integrated_work_back_on_the_queue()
    {
        if (Unavailable) return;
        var store = Store();
        var done = store.Add(WorkItem.Create("already integrated") with { State = WorkItemState.Done });

        Assert.Throws<InvalidOperationException>(() => store.Release(done.Id, "requeued after restart"));

        // The exception on its own proves nothing: bd reopens a closed bead on `--status open` and
        // exits 0, so what matters is that the refusal happened before the write. A bead back in
        // `bd ready` is integrated work the next claim would hand to a station all over again.
        Assert.Equal("closed", Bead(done.Id).Status);
        Assert.DoesNotContain(
            done.Id, Cli().Json<BeadRecord>("ready", "--json", "--limit", "0").Select(bead => bead.Id));
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

    /// <summary>Fails every <c>bd note</c> call while letting everything else through to the real
    /// backlog, the same seam shape as <see cref="InterposesAfterCreate"/> above.</summary>
    private sealed class FailsNoteCalls(string directory, string owner) : BeadsCli(directory, owner)
    {
        public override ShellResult Exec(params string[] args) =>
            args is ["note", ..] ? new ShellResult(1, "", "note rejected for the test", false) : base.Exec(args);
    }

    [Fact]
    public void A_transitions_note_that_beads_refuses_is_logged_rather_than_dropped_in_silence()
    {
        if (Unavailable) return;
        DrainReadyQueue();
        var logged = new List<string>();
        var store = new BeadsWorkItemStore(new FailsNoteCalls(database.Directory, Owner), Owner, logged.Add);
        var item = store.Add(WorkItem.Create("noted") with { State = WorkItemState.Ready });
        var claimed = store.TryClaim(Owner)!;

        store.Transition(claimed, WorkItemState.Ready, "cancelled because the test rejected the note");

        // The reason is not authoritative state, so the transition itself must still have gone
        // through even though beads refused to record why -- state and owner both, the rest of a
        // Ready-bound transition's post-condition.
        Assert.Equal(WorkItemState.Ready, store.Get(item.Id)!.State);
        Assert.Null(store.Get(item.Id)!.Owner);
        Assert.Contains(logged, message => message.Contains(item.Id) && message.Contains("could not be recorded"));
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

        // The counterpart to Reclaim_resolves_a_stale_lease_to_the_item_it_stranded, which builds an
        // expired lease rather than waiting one out: what this pins is that a pass over healthy leases
        // is a no-op that neither throws nor invents items.
        Assert.Empty(Store().Reclaim(TimeSpan.FromMinutes(15)));
    }

    /// <summary>One row of <c>bd export</c>'s JSONL, carrying only what an aged lease needs. Field
    /// names are bd's own, so the naming policy is overridden on every one of them.</summary>
    private sealed record StaleLeaseRow
    {
        [JsonPropertyName("_type")] public string Type => "issue";
        [JsonPropertyName("id")] public required string Id { get; init; }
        [JsonPropertyName("title")] public required string Title { get; init; }
        [JsonPropertyName("status")] public required string Status { get; init; }
        [JsonPropertyName("assignee")] public required string Assignee { get; init; }
        [JsonPropertyName("lease_granted_node")] public required string LeaseGrantedNode { get; init; }
        [JsonPropertyName("started_at")] public required DateTimeOffset StartedAt { get; init; }
        [JsonPropertyName("heartbeat_at")] public required DateTimeOffset HeartbeatAt { get; init; }
        [JsonPropertyName("lease_expires_at")] public required DateTimeOffset LeaseExpiresAt { get; init; }
        [JsonPropertyName("updated_at")] public required DateTimeOffset UpdatedAt { get; init; }
    }

    /// <summary>Leaves <paramref name="item"/> in progress under a lease that expired a minute ago,
    /// the state a worker that died mid-item leaves behind. Not produced by waiting: bd's lease TTL is
    /// a fixed five minutes with no config key, no flag and no environment variable to shorten it.
    ///
    /// <c>bd import</c> is the route. It fills a lease column that is currently null — and a bead moved
    /// to in progress by anything other than a claim has no lease at all — but never overwrites one
    /// already set, which is why the item is not claimed first. It applies a row only when the row's
    /// <c>updated_at</c> is strictly newer than the local copy's, hence the future stamp, and it
    /// defaults every field the row omits rather than leaving it alone, hence the mapper's own write
    /// afterwards to put the item's content back. A plain <c>bd update</c> does not touch the
    /// lease.</summary>
    private void StrandTheLeaseHeldOn(WorkItem item)
    {
        var now = DateTimeOffset.UtcNow;
        var row = FactoryJson.Write(new StaleLeaseRow
        {
            Id = item.Id,
            Title = item.Title,
            Status = BeadMapper.StatusFor(WorkItemState.InProgress),
            Assignee = Owner,
            LeaseGrantedNode = Owner,
            StartedAt = now - TimeSpan.FromMinutes(10),
            HeartbeatAt = now - TimeSpan.FromMinutes(6),
            LeaseExpiresAt = now - TimeSpan.FromMinutes(1),
            UpdatedAt = now + TimeSpan.FromMinutes(1)
        });

        var path = Path.Combine(database.Directory, $"{item.Id}.stale.jsonl");
        File.WriteAllText(path, row + "\n");
        try
        {
            var imported = Cli().Exec("import", "--input", path);
            Assert.True(imported.Ok, imported.Combined);
        }
        finally
        {
            File.Delete(path);
        }

        Store().Update(item with { State = WorkItemState.InProgress });
    }

    [Fact]
    public void Reclaim_resolves_a_stale_lease_to_the_item_it_stranded()
    {
        if (Unavailable) return;
        DrainReadyQueue();
        var store = Store();

        var stranded = store.Add(
            WorkItem.Create("stranded by a worker that died", "so another machine has to get it back")
                with { State = WorkItemState.Ready });
        var live = store.Add(WorkItem.Create("still being worked") with { State = WorkItemState.Ready });
        var claimed = Cli().Exec("update", live.Id, "--claim", "--actor", Owner);
        Assert.True(claimed.Ok, claimed.Combined);

        StrandTheLeaseHeldOn(stranded);

        var reclaimed = store.Reclaim(TimeSpan.Zero);

        // The whole item, not the bare id and previous owner bd reports: Reclaim's contract is a list
        // of work items and the caller reads state and owner off them. Intent lives in the metadata
        // blob rather than in any field bd's reclaim output carries, so asserting it is what proves
        // each id was resolved through a real read and mapped.
        var reported = reclaimed.SingleOrDefault(item => item.Id == stranded.Id);
        Assert.NotNull(reported);
        Assert.Equal("stranded by a worker that died", reported!.Title);
        Assert.Equal("so another machine has to get it back", reported.Intent);

        // Resolved after the reap, so it reports the post-condition the next claimant depends on
        // rather than the in-progress copy the reap started from.
        Assert.Equal(WorkItemState.Ready, reported.State);
        Assert.Null(reported.Owner);

        // A lease that has not expired is not stale, whatever the grace window: --older-than measures
        // from expiry, not from when the claim was taken.
        Assert.DoesNotContain(live.Id, reclaimed.Select(item => item.Id));
        Assert.Equal(WorkItemState.InProgress, store.Get(live.Id)!.State);
    }

    [Fact]
    public void Reclaim_reports_itself_as_scoped_to_this_checkouts_assignee()
    {
        if (Unavailable) return;

        // A live bd reclaim against the throwaway database: bd reports "scoped": true as soon as
        // --assignee is passed, even with nothing stale. This is the live half of the scoping fix; a
        // lease held by another assignee actually surviving a reclaim is probe-evidenced only.
        var response = Cli().JsonObject<BeadsReclaimResponse>(
            [.. BeadMapper.ReclaimArgs(TimeSpan.FromMinutes(15), Owner)]);

        Assert.True(response!.Scoped);
    }

    /// <summary>Fails every <c>bd sync</c> call and counts them, letting everything else through — the
    /// same seam shape as <see cref="FailsNoteCalls"/> above. The count is load-bearing: <c>Sync</c> has
    /// no argument mapper and so no argument test, so an empty body would satisfy a test that only
    /// asserted the absence of a throw.</summary>
    private sealed class FailsSyncCalls(string directory, string owner) : BeadsCli(directory, owner)
    {
        public int SyncCalls { get; private set; }

        public override ShellResult Exec(params string[] args)
        {
            if (args is not ["sync", ..]) return base.Exec(args);

            SyncCalls++;
            return new ShellResult(1, "", "sync failed: fetch from origin/main", false);
        }
    }

    [Fact]
    public void A_sync_beads_could_not_complete_leaves_the_factory_able_to_work_on()
    {
        var cli = new FailsSyncCalls(database.Directory, Owner);

        // The behaviour Sync's contract turns on, and the reason it deliberately ignores its result:
        // beads is a replica model, so an unreachable remote leaves the local database complete. A
        // deployment with no remote at all cannot show this — probed against 1.2.1, bd exits 0 and
        // reports the skip — so the failure has to come from the seam.
        new BeadsWorkItemStore(cli, Owner, _ => { }).Sync();

        // Attempted as well as tolerated. Without the count, a Sync that had stopped calling bd
        // altogether would pass this test and every other one in the suite.
        Assert.Equal(1, cli.SyncCalls);
    }

    [Fact]
    public void Sync_without_a_remote_does_not_throw()
    {
        if (Unavailable) return;

        // A smoke check over the real executable, not coverage of the tolerance above: bd's no-remote
        // path exits 0, so this passes whether or not Sync tolerates a failure.
        Store().Sync();
    }

    [Fact]
    public void A_related_edge_another_tool_added_does_not_block_the_item()
    {
        if (Unavailable) return;
        DrainReadyQueue();
        var store = Store();
        var context = store.Add(WorkItem.Create("background reading") with { State = WorkItemState.Ready });
        var dependent = store.Add(WorkItem.Create("work that merely relates to it") with { State = WorkItemState.Ready });

        var added = Cli().Exec("dep", "add", dependent.Id, context.Id, "--type", "related");
        Assert.True(added.Ok, added.Combined);

        // beads does not withhold a `related` dependent from its own ready queue, so an item the
        // store says is dispatchable must be dispatchable here too — otherwise the factory blocks
        // work the store it calls authoritative is handing out.
        var readyIds = Cli().Json<BeadRecord>("ready", "--json", "--limit", "0").Select(b => b.Id).ToList();
        Assert.Contains(dependent.Id, readyIds);
        Assert.Empty(store.Get(dependent.Id)!.DependsOn);
    }

    [Fact]
    public void An_edit_to_a_field_beads_owns_natively_survives_a_reconcile()
    {
        if (Unavailable) return;
        var store = Store();
        var filed = store.Add(WorkItem.Create("the title as filed", "the intent as filed") with
        {
            State = WorkItemState.Ready,
            Kind = WorkItemKind.Chore
        });

        using var fold = new Fold();
        fold.Open(store);

        store.Update(filed with
        {
            Title = "the title after editing",
            Kind = WorkItemKind.Bug,
            Intent = "the intent after editing",
            AcceptanceCriteria = [AcceptanceCriterion.Command("builds after editing", "dotnet build")]
        });
        fold.Open(store);

        // The fold is corrected from beads on every open, so a field the update never sent to beads
        // is not merely unsaved — the next reconcile actively reverts the edit here.
        var reconciled = fold.State.Items[filed.Id];
        Assert.Equal("the title after editing", reconciled.Title);
        Assert.Equal(WorkItemKind.Bug, reconciled.Kind);
        Assert.Equal("the intent after editing", reconciled.Intent);
        Assert.Equal("builds after editing", Assert.Single(reconciled.AcceptanceCriteria).Statement);
    }

    [Fact]
    public void An_edit_keeps_the_beads_own_description_and_criteria_cells_current()
    {
        if (Unavailable) return;
        var store = Store();
        var filed = store.Add(WorkItem.Create("cells", "the intent as filed") with
        {
            State = WorkItemState.Ready,
            AcceptanceCriteria = [AcceptanceCriterion.Command("as filed", "dotnet build")]
        });

        store.Update(filed with
        {
            Intent = "the intent after editing",
            AcceptanceCriteria = [AcceptanceCriterion.Command("after editing", "dotnet test")]
        });

        // Intent and criteria also travel in the metadata, so the factory's own read hides this.
        // What goes stale is the cell every other beads tool reads — `bd show`, `bd list`, a human —
        // which the spec's mapping table says is the item's intent.
        var bead = Bead(filed.Id);
        Assert.Equal("the intent after editing", bead.Description);
        Assert.Contains("after editing", bead.AcceptanceCriteria);
    }

    [Fact]
    public void Clearing_an_items_intent_clears_the_beads_description_rather_than_leaving_it_stale()
    {
        if (Unavailable) return;
        var store = Store();
        var filed = store.Add(WorkItem.Create("intent to be cleared", "the intent as filed") with
        {
            State = WorkItemState.Ready
        });

        store.Update(filed with { Intent = "" });

        // bd accepts `-d ""` and empties the cell, so an item whose intent is gone must not leave
        // beads asserting the old one to every other reader of the backlog.
        Assert.True(string.IsNullOrEmpty(Bead(filed.Id).Description),
            $"description should be cleared, was '{Bead(filed.Id).Description}'");
    }

    [Fact]
    public void Reconciling_a_real_backlog_twice_writes_nothing_the_second_time()
    {
        if (Unavailable) return;
        var store = Store();
        var filed = store.Add(WorkItem.Create("shared work", "because") with
        {
            State = WorkItemState.Ready,
            AcceptanceCriteria = [AcceptanceCriterion.Command("runs", "dotnet run")]
        });

        var ledger = Path.Combine(TempDir.Create(), "ledger.jsonl");
        using var history = new JsonlRunHistory(ledger);
        var state = history.Replay();

        BacklogReconciler.Reconcile(store, state, history, _ => { });
        var afterFirst = history.ReadFrom(0).Count();
        BacklogReconciler.Reconcile(store, state, history, _ => { });

        // Every field the comparison reads has to round-trip through bd unchanged, or the ledger
        // grows by the whole backlog on every single open.
        Assert.Equal(afterFirst, history.ReadFrom(0).Count());
        Assert.Contains(filed.Id, state.Items.Keys);
    }

    private const string BlockingType = "blocks";

    /// <summary>The ids beads reports as blocking the named bead, read from bd's own output rather
    /// than through the mapper's projection — the projection is what these tests put on trial.</summary>
    private IReadOnlyList<string> BlockersInBeads(string id) =>
    [
        .. Bead(id).Dependencies
            .Where(dependency => dependency.Type == BlockingType)
            .Select(dependency => dependency.DependsOnId!)
    ];

    private IReadOnlyList<string> ReadyInBeads() =>
        [.. Cli().Json<BeadRecord>("ready", "--json", "--limit", "0").Select(bead => bead.Id)];

    [Fact]
    public void An_edge_added_after_filing_reaches_beads_and_survives_a_reconcile()
    {
        if (Unavailable) return;
        var store = Store();
        var blocker = store.Add(WorkItem.Create("the blocker, filed first") with { State = WorkItemState.Ready });
        var dependent = store.Add(WorkItem.Create("dependent, filed with no edges") with { State = WorkItemState.Ready });

        using var fold = new Fold();
        fold.Open(store);

        store.Update(dependent with { DependsOn = [blocker.Id] });
        fold.Open(store);

        // bd's own output, and the type literal this test chose: an edge stored as anything but
        // `blocks` is an edge beads does not withhold the dependent for. Asserted on the edge itself
        // as well as through BlockersInBeads, so the claim does not rest on that helper's filter.
        var stored = Assert.Single(Bead(dependent.Id).Dependencies);
        Assert.Equal("blocks", stored.Type);
        Assert.Equal(blocker.Id, stored.DependsOnId);
        Assert.Equal([blocker.Id], BlockersInBeads(dependent.Id));

        // The fold is corrected from beads on every open, so an edge the update never sent is not
        // merely unsaved in beads — this reconcile reverts it locally too.
        Assert.Equal([blocker.Id], fold.State.Items[dependent.Id].DependsOn);

        // And the post-condition dispatch actually depends on, in both authorities: the dependent is
        // withheld while its blocker is open, and the blocker itself is still offered.
        var readyInBeads = ReadyInBeads();
        var dispatchable = fold.State.Dispatchable().Select(item => item.Id).ToList();
        Assert.DoesNotContain(dependent.Id, readyInBeads);
        Assert.Contains(blocker.Id, readyInBeads);
        Assert.DoesNotContain(dependent.Id, dispatchable);
        Assert.Contains(blocker.Id, dispatchable);
    }

    [Fact]
    public void An_edge_removed_after_filing_is_removed_in_beads()
    {
        if (Unavailable) return;
        var store = Store();
        var blocker = store.Add(WorkItem.Create("a blocker that stops mattering") with { State = WorkItemState.Ready });
        var dependent = store.Add(WorkItem.Create("dependent, filed with an edge") with
        {
            State = WorkItemState.Ready,
            DependsOn = [blocker.Id]
        });
        Assert.Equal([blocker.Id], BlockersInBeads(dependent.Id));

        store.Update(dependent with { DependsOn = [] });

        Assert.Empty(BlockersInBeads(dependent.Id));

        // The post-condition: beads offers the item again. An edge dropped from the item but left in
        // beads holds the work back everywhere, and the next reconcile puts the edge back locally.
        Assert.Contains(dependent.Id, ReadyInBeads());
    }

    [Fact]
    public void An_update_leaves_a_non_blocking_edge_another_tool_added_in_place()
    {
        if (Unavailable) return;
        var store = Store();
        var context = store.Add(WorkItem.Create("background reading, linked by a human") with { State = WorkItemState.Ready });
        var dependent = store.Add(WorkItem.Create("work a human merely linked") with { State = WorkItemState.Ready });

        var added = Cli().Exec("dep", "add", dependent.Id, context.Id, "--type", "related");
        Assert.True(added.Ok, added.Combined);

        // The item carries no DependsOn, because a `related` edge is not a blocker and the mapper
        // never reads one as one. `bd dep remove` is type-blind — it deletes whatever edge joins the
        // pair — so a removal pass driven from "every edge not in DependsOn" erases this silently.
        store.Update(dependent with { DependsOn = [] });

        var edge = Assert.Single(Bead(dependent.Id).Dependencies);
        Assert.Equal("related", edge.Type);
        Assert.Equal(context.Id, edge.DependsOnId);

        // And the item it is on is still dispatchable, so nothing about keeping the edge holds work back.
        Assert.Contains(dependent.Id, ReadyInBeads());
    }

    [Fact]
    public void An_edge_beads_refuses_fails_loudly_rather_than_leaving_the_two_disagreeing()
    {
        if (Unavailable) return;
        var store = Store();
        var first = store.Add(WorkItem.Create("first half of a cycle") with { State = WorkItemState.Ready });
        var second = store.Add(WorkItem.Create("second half of a cycle") with { State = WorkItemState.Ready });
        store.Update(second with { DependsOn = [first.Id] });

        // bd checks each edge for a cycle and refuses one that closes a loop. Swallowing that would
        // leave Dispatchable() withholding an item bd ready hands straight out — the authority
        // disagreeing with itself, which is the defect this whole diff exists to close.
        Assert.Throws<InvalidOperationException>(() => store.Update(first with
        {
            Title = RenamedByTheRefusedUpdate,
            DependsOn = [second.Id]
        }));

        // Nothing written to the graph: bd rejects the edge before storing it, so the refusal must
        // not have left a half-applied graph behind either.
        Assert.Empty(BlockersInBeads(first.Id));
        Assert.Equal([first.Id], BlockersInBeads(second.Id));

        // The field half of the same update did commit, because the edge diff runs after it. That
        // asymmetry is deliberate and is why throwing costs nothing durable: the fields are in beads,
        // and the un-applied edge is recomputed from beads by the next update rather than lost.
        Assert.Equal(RenamedByTheRefusedUpdate, Bead(first.Id).Title);
    }

    private const string RenamedByTheRefusedUpdate = "renamed by the update whose edge was refused";

    [Fact]
    public void An_update_that_drops_a_blocker_leaves_a_foreign_edge_on_the_same_item_alone()
    {
        if (Unavailable) return;
        var logged = new List<string>();
        var store = new BeadsWorkItemStore(Cli(), Owner, logged.Add);
        var blocker = store.Add(WorkItem.Create("a blocker the item stops waiting on") with { State = WorkItemState.Ready });
        var context = store.Add(WorkItem.Create("background reading a human linked") with { State = WorkItemState.Ready });
        var dependent = store.Add(WorkItem.Create("an item with one edge of each kind") with
        {
            State = WorkItemState.Ready,
            DependsOn = [blocker.Id]
        });

        var added = Cli().Exec("dep", "add", dependent.Id, context.Id, "--type", "related");
        Assert.True(added.Ok, added.Combined);
        Assert.Equal(2, Bead(dependent.Id).Dependencies.Count);

        // One call that has to do both halves of the discrimination at once: drop the blocking edge
        // the item no longer carries, and leave the non-blocking one it never carried. Beads allows
        // only one edge per ordered pair, so the two necessarily point at different items — which is
        // exactly the shape in which a type-blind removal pass takes the foreign edge with it.
        store.Update(dependent with { DependsOn = [] });

        var survivor = Assert.Single(Bead(dependent.Id).Dependencies);
        Assert.Equal("related", survivor.Type);
        Assert.Equal(context.Id, survivor.DependsOnId);

        // And the post-condition: dropping the blocker really did unblock the work, so the removal
        // reached beads rather than merely being absent from the item.
        Assert.Contains(dependent.Id, ReadyInBeads());

        // The removal is reported. A snapshot carries no base revision, so this pass cannot tell an
        // edge this checkout dropped from one another actor added since the item was read — it deletes
        // either. The log line is the only record that the row existed, so it is asserted, not assumed.
        Assert.Contains(logged, line => line.Contains(dependent.Id) && line.Contains(blocker.Id));
    }
}
