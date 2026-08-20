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

    // bd is on PATH in this environment, so these run for real. A machine without bd does not skip
    // these tests -- xunit 2.9.2 has no dynamic skip, so each one returns here and reports as passed.
    // That keeps the rest of the suite offline-clean at the cost of a green that asserts nothing, which
    // is what BeadsAvailabilityTests exists to turn into a single red.
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
        private readonly JsonlRunHistory _history;

        public Fold(params string[] existingLines)
        {
            var path = Path.Combine(TempDir.Create(), "ledger.jsonl");

            // Written as text, never through the ports: a fold that already holds values the current
            // code would refuse is exactly what the real cutover opens, and any line this test built
            // through a WorkItem would be normalised before it ever reached the file.
            if (existingLines.Length > 0) File.WriteAllLines(path, existingLines);

            _history = new JsonlRunHistory(path);
            State = _history.Replay();
        }

        public FactoryState State { get; }

        public void Open(IWorkItemStore store, Action<string>? log = null) =>
            BacklogReconciler.Reconcile(store, State, _history, log ?? (_ => { }));

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

    private const string HumanIntent = "the intent a human rewrote by hand";

    [Fact]
    public void AHumansEditToTheBeadsOwnDescriptionIsReadReportedAndKept()
    {
        if (Unavailable) return;
        var store = Store();
        var filed = store.Add(WorkItem.Create("shared with a human", "the intent as filed") with
        {
            State = WorkItemState.Ready
        });

        using var fold = new Fold();
        fold.Open(store);

        // One command a human runs against the shared database, on the cell beads owns natively.
        var edited = Cli().Exec("update", filed.Id, "-d", HumanIntent, "--actor", "a-human");
        Assert.True(edited.Ok, edited.Combined);

        // 1. The read adopts it. MetadataFor also writes intent into the metadata blob, and UpdateArgs
        //    sends the same value to -d on every write, so after any factory write the two agree by
        //    construction — which is exactly why preferring the metadata copy made the factory read
        //    its own stale echo instead of the human's edit, and why a difference between the two can
        //    only have come from outside.
        Assert.Equal(HumanIntent, store.Get(filed.Id)!.Intent);

        // 2. Reconcile reports it. SharedState compares the mapped projection, so an edit the read
        //    does not see is an edit the fold is never told about either — no correction, no log line.
        var logged = new List<string>();
        fold.Open(store, logged.Add);

        Assert.Equal(HumanIntent, fold.State.Items[filed.Id].Intent);
        Assert.Contains(logged, message => message.Contains("reconciled"));

        // 3. And the next factory write leaves it standing. This is the half that made the defect
        //    destructive rather than merely invisible: -d is unconditional, so the stale value went
        //    straight back over the human's text at exit 0 with nothing logged.
        store.Update(fold.State.Items[filed.Id] with { Title = "retitled by the factory" });

        Assert.Equal(HumanIntent, Bead(filed.Id).Description);
    }

    [Fact]
    public void AFactoryItemThatHasCriteriaOfItsOwnStillOverwritesAHumansAcceptanceCell()
    {
        if (Unavailable) return;
        var store = Store();
        var filed = store.Add(WorkItem.Create("criteria of its own") with
        {
            State = WorkItemState.Ready,
            AcceptanceCriteria = [AcceptanceCriterion.Command("as filed", "dotnet build")]
        });

        var edited = Cli().Exec("update", filed.Id, "--acceptance", ForeignCriterion, "--actor", "a-human");
        Assert.True(edited.Ok, edited.Combined);

        store.Update(store.Get(filed.Id)!);

        // The accepted asymmetry, documented at UpdateArgs and until now asserted nowhere. --acceptance
        // is conditional, which protects a bead with no factory criteria; an item that has its own
        // renders them over the human's cell, because the structured criteria in the metadata are the
        // authority for what the factory believes and there is nowhere else to put them. Asserted so
        // the cost is a decision on record rather than a surprise, and so making the write
        // unconditional cannot pass unnoticed.
        Assert.DoesNotContain(ForeignCriterion, Bead(filed.Id).AcceptanceCriteria);
        Assert.Contains("as filed", Bead(filed.Id).AcceptanceCriteria);
    }

    [Fact]
    public void UpdatingABeadAnotherToolFiledKeepsTheAcceptanceCriteriaItWrote()
    {
        if (Unavailable) return;

        var id = AForeignBeadTheFactoryHasClaimedAndUpdated("an epic a human filed");

        // Asserted from bd's own output, because the mapper's projection is what is under suspicion.
        // ToWorkItem reads criteria only from the factory's metadata blob, so a foreign bead's arrive
        // empty — and writing that emptiness back would erase beads' own cell for good.
        Assert.Equal(ForeignCriterion, Bead(id).AcceptanceCriteria);
    }

    [Fact]
    public void UpdatingABeadAnotherToolFiledKeepsTheTypeItWasGiven()
    {
        if (Unavailable) return;

        var id = AForeignBeadTheFactoryHasClaimedAndUpdated("a milestone a human filed");

        // Asserted from bd's own output for the same reason as the criteria above: KindFor is what is
        // under suspicion. An epic flattened to WorkItemKind.Feature is written straight back out as
        // `feature`, so a type the factory cannot name is a type it destroys on first touch.
        Assert.Equal(ForeignType, Bead(id).IssueType);
    }

    [Fact]
    public void AddThenGetRoundTripsAWorkItem()
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
    public void AddFilesAProposalAsADraftRatherThanAsReadyWork()
    {
        if (Unavailable) return;
        var store = Store();

        var item = store.Add(WorkItem.Create("a proposal"));

        Assert.Equal(WorkItemState.Draft, store.Get(item.Id)!.State);
    }

    [Fact]
    public void AddLeavesABeadAnotherCheckoutClaimedMidFilingAlone()
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
    public void AddStillFailsLoudlyWhenTheSecondWriteFailsForAnyOtherReason()
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
    public void AddThenGetRoundTripsDependencies()
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
    public void GetReturnsNullForAnIdTheBacklogDoesNotKnow()
    {
        if (Unavailable) return;

        Assert.Null(Store().Get("wi-000000000000"));
    }

    [Fact]
    public void TryClaimMarksTheItemInProgressAndAssignsItToTheNamedOwner()
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
    public void TryClaimStampsTheLeaseWithThisCheckoutsNodeId()
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
    public void TryClaimTakesALeaseTheFactoryCanRefresh()
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
    public void TryClaimWithholdsAnItemWithAnUnmetDependency()
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
    public void ADraftItemIsNeverClaimed()
    {
        if (Unavailable) return;
        DrainReadyQueue();
        var store = Store();

        store.Add(WorkItem.Create("proposal"));

        Assert.Null(store.TryClaim(Owner));
    }

    [Fact]
    public void ReleaseReturnsAClaimedItemToTheQueueAndDropsItsLease()
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
    public void ReleaseRequeuesAnItemThatAStationHasAlreadyMovedPastInProgress()
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
    public void TransitioningAClaimedItemBackToReadyLeavesItClaimableAgain()
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
    public void TransitioningAClaimedItemBackToReadyReportsNobodyHoldingIt()
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
    public void ReleaseRefusesToPutIntegratedWorkBackOnTheQueue()
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
    public void ReleaseDoesNothingForAnIdTheBacklogDoesNotKnow()
    {
        if (Unavailable) return;

        Store().Release("wi-000000000000", "requeued after restart");
    }

    [Fact]
    public void HeartbeatStaysQuietForAnItemThisNodeDoesNotHold()
    {
        if (Unavailable) return;
        var logged = new List<string>();
        var store = new BeadsWorkItemStore(Cli(), Owner, logged.Add);
        var item = store.Add(WorkItem.Create("unheld") with { State = WorkItemState.Ready });

        // bd refuses a heartbeat on a bead that is not in progress here -- probed: exit 1, `issue not
        // claimable: <id> status open`. That is the expected refusal every station past in_progress
        // produces, so it must neither throw nor be reported.
        store.Heartbeat(item.Id);

        Assert.Empty(logged);
    }

    private sealed class FailsHeartbeatCalls(string directory, string owner) : BeadsCli(directory, owner)
    {
        public override ShellResult Exec(params string[] args) =>
            args is ["heartbeat", ..]
                ? new ShellResult(1, "", "Error: unknown command \"heartbeat\" for \"bd\"", false)
                : base.Exec(args);
    }

    [Fact]
    public void AHeartbeatFailingForAnyOtherReasonIsReported()
    {
        if (Unavailable) return;
        DrainReadyQueue();
        var logged = new List<string>();
        var store = new BeadsWorkItemStore(new FailsHeartbeatCalls(database.Directory, Owner), Owner, logged.Add);
        var item = store.Add(WorkItem.Create("held but unrefreshable") with { State = WorkItemState.Ready });
        var claimed = store.TryClaim(Owner)!;
        Assert.Equal(item.Id, claimed.Id);

        // A renamed command after a bd upgrade, injected here because it is the failure the silence was
        // hiding. The heartbeat is the only thing holding the claim, so this is the lease expiring
        // mid-station and another machine taking the work -- previously with nothing anywhere saying
        // why, because the expected refusal above justified discarding the result entirely.
        store.Heartbeat(claimed.Id);

        Assert.Contains(logged, message => message.Contains(claimed.Id) && message.Contains("unknown command"));
    }

    [Fact]
    public void TransitionRefusesAMoveTheStateMachineDoesNotAllow()
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
    public void ATransitionsNoteThatBeadsRefusesIsLoggedRatherThanDroppedInSilence()
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
    public void AllReportsWorkInEveryStatusIncludingClosedAndDraft()
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

    /// <summary>Files a bead and then has a human move it to one of bd's own statuses, the way one
    /// command at a terminal does. Returns the id; the bead is left in that status.</summary>
    private string ABeadAHumanMovedTo(string status, string title)
    {
        var id = Ids.New("wi");
        var filed = Cli().Exec("create", title, "--id", id, "-t", "task", "--json");
        Assert.True(filed.Ok, filed.Combined);

        // Exit 0: this is one command a human runs, not a corruption a test had to manufacture.
        var moved = Cli().Exec("update", id, "--status", status);
        Assert.True(moved.Ok, moved.Combined);

        return id;
    }

    [Theory]
    [InlineData("deferred")]
    [InlineData("pinned")]
    [InlineData("hooked")]
    public void AStatusAHumanSetDoesNotStopTheFactoryOpening(string status)
    {
        if (Unavailable) return;
        var store = Store();
        var id = ABeadAHumanMovedTo(status, $"a bead a human moved to {status}");

        // All() maps every bead in the database and Reconcile is called outside any tolerance, so a
        // throw here is every factory command dying on every machine sharing the backlog, with no way
        // to recover from inside the factory. This is the halt, not the mapping.
        using var fold = new Fold();
        fold.Open(store);

        Assert.Contains(id, store.All().Select(item => item.Id));
        Assert.Equal(WorkItemState.Blocked, fold.State.Items[id].State);
    }

    [Theory]
    [InlineData("deferred")]
    [InlineData("pinned")]
    [InlineData("hooked")]
    public void AnUpdateKeepsTheStatusAHumanSet(string status)
    {
        if (Unavailable) return;
        var store = Store();
        var id = ABeadAHumanMovedTo(status, $"a bead a human moved to {status}");

        store.Update(store.Get(id)! with { Title = "retitled by the factory" });

        // Asserted from bd's own output, because the mapper's projection is what is under suspicion:
        // reading the status as Blocked and writing that back is the same destruction this branch
        // closed for issue_type and for acceptance criteria. The rest of the update still lands.
        var bead = Bead(id);
        Assert.Equal(status, bead.Status);
        Assert.Equal("retitled by the factory", bead.Title);
    }

    [Theory]
    [InlineData("deferred")]
    [InlineData("pinned")]
    [InlineData("hooked")]
    public void AStatusAHumanSetIsDispatchedByNeitherAuthority(string status)
    {
        if (Unavailable) return;
        var store = Store();
        DrainReadyQueue();
        var id = ABeadAHumanMovedTo(status, $"a bead a human moved to {status}");

        using var fold = new Fold();
        fold.Open(store);

        // Both authorities, because either one alone would let the item be worked on: bd withholds it
        // from `bd ready` (probed), and the factory reads it as Blocked, which Dispatchable() excludes.
        Assert.DoesNotContain(id, ReadyInBeads());
        Assert.DoesNotContain(id, fold.State.Dispatchable().Select(item => item.Id));
        Assert.Null(store.TryClaim(Owner));
    }

    [Fact]
    public void AStatusTheFactoryHasTakenOverIsNotCarriedAnyFurther()
    {
        if (Unavailable) return;
        var store = Store();
        var id = ABeadAHumanMovedTo("pinned", "a bead a human pinned, then activated");
        var pinned = store.Get(id)!;
        Assert.Equal("pinned", pinned.StoreStatus);

        var activated = store.Transition(pinned, WorkItemState.Ready, "activated by the operator");

        // The write sent --status, so beads' own word is gone and the item must stop carrying it.
        Assert.Equal("open", Bead(id).Status);
        Assert.Null(activated.StoreStatus);

        // The consequence if it kept carrying it: the item's state is Blocked again here, which is what
        // `pinned` reads as, so the suppression would fire on the strength of a word beads no longer
        // holds and leave the two disagreeing for the rest of the run.
        store.Transition(activated, WorkItemState.Blocked, "blocked after activation");

        Assert.Equal("blocked", Bead(id).Status);
    }

    [Fact]
    public void ALegacyPriorityOnAnItemDoesNotHaltTheBacklogWrite()
    {
        if (Unavailable) return;
        var store = Store();
        var filed = store.Add(WorkItem.Create("work filed before the band was narrowed") with
        {
            State = WorkItemState.Ready
        });

        // bd refuses -p 100 with exit 1 and writes nothing (probed), and Write turns any non-zero exit
        // into a throw that GuardedWorkItemStore raises as a WorkItemStoreException -- so on the real
        // checkout this is a factory that halts on the first update of a pre-existing item, and halts
        // again on every retry.
        store.Update(filed with { Priority = 100 });

        Assert.InRange(Bead(filed.Id).Priority, Priorities.Highest, Priorities.Lowest);
    }

    [Fact]
    public void AFoldAlreadyHoldingALegacyPriorityOpensReconcilesAndUpdatesWithoutHalting()
    {
        if (Unavailable) return;
        var store = Store();
        var filed = store.Add(WorkItem.Create("the item the cutover meets first") with
        {
            State = WorkItemState.Ready
        });

        using var fold = new Fold(LegacyFiledLine(filed.Id, filed.Title, priority: 100));

        // The load-bearing assertion. Normalised on the way in, before beads is consulted at all: the
        // fold is what `factory ls`, the budget and the orphan requeue read, and it is the copy a write
        // is built from. The update below is belt-and-braces -- probed, a reconcile against a bead
        // beads already holds also corrects the fold's priority (100 -> 2), so this open would survive
        // the write on its own; it is the item beads does not hold at all, the cutover's own case,
        // where the fold's own value is the only one there is.
        Assert.Equal(Priorities.Lowest, fold.State.Items[filed.Id].Priority);

        fold.Open(store);
        store.Update(fold.State.Items[filed.Id]);

        Assert.InRange(Bead(filed.Id).Priority, Priorities.Highest, Priorities.Lowest);
    }

    /// <summary>One <c>work_item_filed</c> line in the shape this repository's own ledger holds 87
    /// of, with the legacy priority it holds them at.</summary>
    private static string LegacyFiledLine(string id, string title, int priority) =>
        $$"""
        {"type":"work_item_filed","item":{"id":"{{id}}","title":"{{title}}","kind":"Feature",
         "state":"Ready","priority":{{priority}},"createdAt":"2026-08-13T06:23:37+00:00",
         "updatedAt":"2026-08-13T06:23:37+00:00"},
         "eventId":"evt_645fd1b2a793","at":"2026-08-13T06:23:37+00:00","seq":1}
        """.ReplaceLineEndings("");

    [Fact]
    public void ReclaimRevertsNothingWhileEveryLeaseIsLive()
    {
        if (Unavailable) return;

        // The counterpart to ReclaimResolvesAStaleLeaseToTheItemItStranded, which builds an
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
    private void StrandTheLeaseHeldOn(WorkItem item, string? heldBy = null)
    {
        var holder = heldBy ?? Owner;
        var now = DateTimeOffset.UtcNow;
        var row = FactoryJson.Write(new StaleLeaseRow
        {
            Id = item.Id,
            Title = item.Title,
            Status = BeadMapper.StatusFor(WorkItemState.InProgress),
            Assignee = holder,
            LeaseGrantedNode = holder,
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

        // Puts back the content `bd import` defaulted away. Only for this checkout's own bead: a
        // factory write onto a bead another machine holds is the very thing PF2 stopped doing, and the
        // foreign case asserts on status, assignee and the reclaim result rather than on content.
        if (heldBy is null) Store().Update(item with { State = WorkItemState.InProgress });
    }

    private const string OtherMachine = "other-machine";

    [Fact]
    public void ReclaimLeavesAStaleLeaseAnotherMachineHoldsAlone()
    {
        if (Unavailable) return;
        DrainReadyQueue();
        var store = Store();
        var foreign = store.Add(
            WorkItem.Create("stranded on another machine") with { State = WorkItemState.Ready });

        StrandTheLeaseHeldOn(foreign, heldBy: OtherMachine);

        var reclaimed = store.Reclaim(TimeSpan.Zero);

        // Critical 2's own acceptance criterion — "a foreign lease is not reclaimed" — asserted on the
        // reap itself rather than on the flags. The argument test and
        // TryClaimStampsTheLeaseWithThisCheckoutsNodeId each pin one guard's precondition;
        // this pins the composed result, which is what the plan actually asked for. It became
        // affordable when Task 5 found the `bd import` route: bd's lease TTL is a fixed five minutes,
        // which is what made a genuinely stale lease unaffordable and is the premise this test used to
        // be excused by.
        Assert.DoesNotContain(foreign.Id, reclaimed.Select(item => item.Id));

        // Asserted from bd's own output, because the point is that nothing was written: reclaiming a
        // foreign lease would set the bead back to open and drop the assignee, and its holder — which
        // may be working on it right now — would lose the work to whichever machine claimed next.
        var bead = Bead(foreign.Id);
        Assert.Equal(BeadMapper.StatusFor(WorkItemState.InProgress), bead.Status);
        Assert.Equal(OtherMachine, bead.Assignee);

        // The detail the branch does not state anywhere and this test depends on: the two halves of
        // the Task 2 fix mask each other. BeadsCli always sets BEADS_NODE_ID, so bd's replica guard
        // alone spares this lease even with --assignee removed, and --assignee alone spares it with
        // the node id unset. Only removing both reproduces the phase-3 defect, so a mutation of either
        // one on its own will not redden this — which is exactly why the two argument-level tests have
        // to stay rather than being folded into this one.
    }

    [Fact]
    public void ReclaimResolvesAStaleLeaseToTheItemItStranded()
    {
        if (Unavailable) return;
        DrainReadyQueue();
        var store = Store();

        var stranded = store.Add(
            WorkItem.Create("stranded by a worker that died", "so another machine has to get it back")
                with
            { State = WorkItemState.Ready });
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
    public void ReclaimReportsItselfAsScopedToThisCheckoutsAssignee()
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
    public void ASyncBeadsCouldNotCompleteLeavesTheFactoryAbleToWorkOn()
    {
        var cli = new FailsSyncCalls(database.Directory, Owner);
        var logged = new List<string>();

        // The behaviour Sync's contract turns on: beads is a replica model, so an unreachable remote
        // leaves the local database complete. A deployment with no remote at all cannot show this —
        // probed against 1.2.1, bd exits 0 and reports the skip — so the failure has to come from the
        // seam.
        new BeadsWorkItemStore(cli, Owner, logged.Add).Sync();

        // Attempted as well as tolerated. Without the count, a Sync that had stopped calling bd
        // altogether would pass this test and every other one in the suite.
        Assert.Equal(1, cli.SyncCalls);

        // Tolerated is not the same as unreported. Surfacing a Degraded state is the sync-gate plan's
        // task; without at least this line, a shared backlog stops replicating in total silence, and
        // goal 1 — the backlog surviving the loss of a machine — quietly stops being true.
        Assert.Contains(logged, message => message.Contains("sync failed"));
    }

    [Fact]
    public void SyncWithoutARemoteReportsNothing()
    {
        if (Unavailable) return;
        var logged = new List<string>();

        // Over the real executable: bd's no-remote path exits 0 and reports the skip, which is the
        // path this repository — and every solo deployment — actually takes. Warning on it would put a
        // line nobody can act on in front of every operator on every run.
        new BeadsWorkItemStore(Cli(), Owner, logged.Add).Sync();

        Assert.Empty(logged);
    }

    [Fact]
    public void ARelatedEdgeAnotherToolAddedDoesNotBlockTheItem()
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
    public void AnEditToAFieldBeadsOwnsNativelySurvivesAReconcile()
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
    public void AnEditKeepsTheBeadsOwnDescriptionAndCriteriaCellsCurrent()
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
    public void ClearingAnItemsIntentClearsTheBeadsDescriptionRatherThanLeavingItStale()
    {
        if (Unavailable) return;
        var store = Store();
        var filed = store.Add(WorkItem.Create("intent to be cleared", "the intent as filed") with
        {
            State = WorkItemState.Ready
        });

        store.Update(filed with { Intent = "" });

        // Read once and held: C# evaluates the interpolated failure message eagerly, so naming Bead()
        // inside it spent a second bd subprocess on every passing run.
        var description = Bead(filed.Id).Description;

        // bd accepts `-d ""` and empties the cell, so an item whose intent is gone must not leave
        // beads asserting the old one to every other reader of the backlog.
        Assert.True(string.IsNullOrEmpty(description),
            $"description should be cleared, was '{description}'");
    }

    [Fact]
    public void ReconcilingARealBacklogTwiceWritesNothingTheSecondTime()
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
    public void AnEdgeAHumanAlreadyTypedAsAnotherTypeIsLoggedRatherThanHalting()
    {
        if (Unavailable) return;
        var logged = new List<string>();
        var store = new BeadsWorkItemStore(Cli(), Owner, logged.Add);
        var blocker = store.Add(WorkItem.Create("the blocker") with { State = WorkItemState.Ready });
        var dependent = store.Add(WorkItem.Create("the dependent") with { State = WorkItemState.Ready });

        // One ordinary edit by another actor. beads allows at most one edge per ordered pair, so this
        // is enough to make the factory's own `dep add` on the same pair exit 1 (probed).
        var typed = Cli().Exec("dep", "add", dependent.Id, blocker.Id, "--type", "related");
        Assert.True(typed.Ok, typed.Combined);

        store.Update(dependent with { DependsOn = [blocker.Id] });

        // Not a halt: the field write has already committed by the time the edge diff runs, so a throw
        // here leaves the mirror unrun and the fold never learning the change -- and the next open
        // repairs the fold only for the next update to halt again in the same place.
        Assert.Contains(logged, message => message.Contains(dependent.Id) && message.Contains("related"));

        // And the human's edge is still theirs, untouched.
        var stored = Assert.Single(Bead(dependent.Id).Dependencies);
        Assert.Equal("related", stored.Type);
    }

    [Fact]
    public void AnEdgeBeadsRefusesAsBrokenIsStillLoud()
    {
        if (Unavailable) return;
        var store = Store();
        var dependent = store.Add(WorkItem.Create("names a blocker beads does not know") with
        {
            State = WorkItemState.Ready
        });

        // The other side of the same discrimination: an id beads has never heard of is a defect in the
        // caller, not another actor's edit, and must not be swallowed alongside the occupied pair.
        var refused = Assert.Throws<InvalidOperationException>(
            () => store.Update(dependent with { DependsOn = ["wi-000000000000"] }));

        Assert.Contains("wi-000000000000", refused.Message);
    }

    [Fact]
    public void AnEdgeAddedAfterFilingReachesBeadsAndSurvivesAReconcile()
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
    public void AnEdgeRemovedAfterFilingIsRemovedInBeads()
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
    public void AnUpdateLeavesANonBlockingEdgeAnotherToolAddedInPlace()
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
    public void AnEdgeBeadsRefusesFailsLoudlyRatherThanLeavingTheTwoDisagreeing()
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
    public void AnUpdateThatDropsABlockerLeavesAForeignEdgeOnTheSameItemAlone()
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
