using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

/// <summary>
/// <see cref="LedgerMirroringWorkItemStore"/> against a fake backing store, isolated from every
/// caller that happens to follow a claim or a Ready-bound transition with a second, unconditional
/// <c>Update</c> — the beads-backed integration tests never observe <c>MirrorChange</c> on its own
/// for exactly that reason, since every current call site does follow up that way.
/// </summary>
public class LedgerMirroringWorkItemStoreTests
{
    private sealed class FakeStore : IWorkItemStore
    {
        public WorkItem? ClaimResult;
        public IReadOnlyList<WorkItem> ReclaimResult = [];
        public WorkItem Add(WorkItem item) => item;
        public WorkItem Update(WorkItem item) => item;
        public WorkItem Transition(WorkItem item, WorkItemState target, string? reason) => item with { State = target };
        public WorkItem? Get(string id) => null;
        public IReadOnlyList<WorkItem> All() => [];
        public WorkItem? TryClaim(string owner) => ClaimResult;
        public void Heartbeat(string id) { }
        public void Release(string id, string reason) { }
        public void Sync() { }
        public IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan) => ReclaimResult;
    }

    /// <summary>A ledger that keeps nothing. <paramref name="fault"/> makes every append fail
    /// with it, for the faults no real <see cref="IRunHistory"/> in this repository can be made to
    /// raise.</summary>
    private sealed class NullHistory(Exception? fault = null) : IRunHistory
    {
        public void Append(FactoryEvent evt)
        {
            if (fault is not null) throw fault;
        }
        public IEnumerable<FactoryEvent> ReadFrom(long afterSeq) => [];
        public IReadOnlyList<RunRecord> RunsForItem(string itemId) => [];
        public IReadOnlyList<RunRecord> RunsForStation(string stationId) => [];
        public SpendTotals Totals() => SpendTotals.Empty;
        public BudgetRestoreView ForBudget() => BudgetRestoreView.Empty;
        public IReadOnlyDictionary<string, string> Champions() => new Dictionary<string, string>();
        public void Dispose() { }
    }

    [Fact]
    public void ClaimingAnItemTheFoldAlreadyHoldsCarriesTheNewOwnerIntoIt()
    {
        var state = FactoryState.Replay([]);
        var item = WorkItem.Create("already known to the fold");
        state.Apply(new WorkItemFiled(item));

        var inner = new FakeStore { ClaimResult = item with { State = WorkItemState.InProgress, Owner = "claimant" } };
        var mirror = new LedgerMirroringWorkItemStore(inner, new NullHistory(), state, _ => { });

        mirror.TryClaim("claimant");

        // FactoryState.ApplyLocked applies a WorkItemStateChanged as State + UpdatedAt only, so an
        // item the fold already knew about before the claim only learns the new owner if the mirror
        // chose WorkItemUpdated instead.
        Assert.Equal("claimant", state.Items[item.Id].Owner);
    }

    [Fact]
    public void ReleasingAClaimedItemClearsItsOwnerInTheFold()
    {
        var state = FactoryState.Replay([]);
        var item = WorkItem.Create("claimed then released") with
        {
            State = WorkItemState.InProgress,
            Owner = "claimant"
        };
        state.Apply(new WorkItemFiled(item));

        var mirror = new LedgerMirroringWorkItemStore(new FakeStore(), new NullHistory(), state, _ => { });

        // inner.Release returns nothing, so unlike every other mutating call, the mirror has no
        // "after" item from the store to compare against — only its own fold copy to correct.
        mirror.Release(item.Id, "requeued after restart");

        var released = state.Items[item.Id];
        Assert.Null(released.Owner);
        Assert.Equal(WorkItemState.Ready, released.State);
    }

    [Fact]
    public void ReclaimingAStaleLeaseKeepsTheRunStateTheBacklogDoesNotStore()
    {
        var state = FactoryState.Replay([]);
        var item = WorkItem.Create("stalled mid-flight") with
        {
            State = WorkItemState.InProgress,
            Owner = "claimant",
            Attempts = 2,
            LastError = "the station died",
            SpentUsd = 0.75m,
            Worktree = "/tmp/worktrees/stalled"
        };
        state.Apply(new WorkItemFiled(item));

        // What the backlog store hands back: reclaim re-reads the bead, so the item arrives mapped
        // out of beads with its lease dropped and none of this checkout's run state.
        var inner = new FakeStore
        {
            ReclaimResult = [new WorkItem { Id = item.Id, Title = item.Title, State = WorkItemState.Ready }]
        };
        var mirror = new LedgerMirroringWorkItemStore(inner, new NullHistory(), state, _ => { });

        var reclaimed = mirror.Reclaim(TimeSpan.FromMinutes(15)).Single();

        Assert.Equal(2, reclaimed.Attempts);
        Assert.Equal("the station died", reclaimed.LastError);
        Assert.Equal(0.75m, reclaimed.SpentUsd);
        Assert.Equal("/tmp/worktrees/stalled", reclaimed.Worktree);

        // The reaped lease is still the store's news to deliver.
        Assert.Equal(WorkItemState.Ready, reclaimed.State);
        Assert.Null(reclaimed.Owner);

        var folded = state.Items[item.Id];
        Assert.Equal(2, folded.Attempts);
        Assert.Equal(0.75m, folded.SpentUsd);
        Assert.Equal("the station died", folded.LastError);
        Assert.Equal("/tmp/worktrees/stalled", folded.Worktree);
    }

    // A ledger this process may not write is D2's named tolerable failure: the backlog store has
    // already committed by the time the mirror runs, so the append is the only thing lost and the
    // next reconcile puts it back. What makes it a live risk rather than a theoretical one is the
    // exception's type, pinned below.

    /// <summary>A ledger file this process may not write, in a temp directory of its own. Disposing
    /// clears the attribute before deleting, because <see cref="TempDir.Delete"/> tolerates only
    /// <see cref="IOException"/> and a recursive delete over a read-only tree can raise
    /// <see cref="UnauthorizedAccessException"/> instead.</summary>
    private sealed class UnwritableLedger : IDisposable
    {
        private readonly string _root = TempDir.Create();

        public UnwritableLedger()
        {
            LedgerPath = Path.Combine(_root, "ledger.jsonl");
            File.WriteAllText(LedgerPath, "");
            File.SetAttributes(LedgerPath, FileAttributes.ReadOnly);
        }

        public string LedgerPath { get; }

        public void Dispose()
        {
            File.SetAttributes(LedgerPath, FileAttributes.Normal);
            TempDir.Delete(_root);
        }
    }

    [Fact]
    public void AppendingToALedgerThisProcessMayNotWriteReportsAccessDenied()
    {
        using var ledger = new UnwritableLedger();
        using var history = new JsonlRunHistory(ledger.LedgerPath);

        // The type is the whole point of the mirror's catch set, so it is pinned against a real
        // FileStream rather than assumed: UnauthorizedAccessException is a SystemException and not
        // an IOException, so a catch built around IOException alone does not hold this.
        Assert.Throws<UnauthorizedAccessException>(
            () => history.Append(new WorkItemFiled(WorkItem.Create("unwritable"))));
    }

    [Fact]
    public void AClaimSurvivesALedgerThisProcessMayNotWrite()
    {
        var state = FactoryState.Replay([]);
        var item = WorkItem.Create("claimed while the ledger is read-only");
        state.Apply(new WorkItemFiled(item));

        var logged = new List<string>();
        using var ledger = new UnwritableLedger();
        using var history = new JsonlRunHistory(ledger.LedgerPath);
        var inner = new FakeStore { ClaimResult = item with { State = WorkItemState.InProgress, Owner = "claimant" } };
        var mirror = new LedgerMirroringWorkItemStore(inner, history, state, logged.Add);

        // The bead is already claimed by the time this runs, so letting the local ledger's refusal
        // out makes GuardedWorkItemStore halt the factory and blame the backlog provider for it.
        var claimed = mirror.TryClaim("claimant");

        Assert.Equal(item.Id, claimed?.Id);
        Assert.Contains(logged, message => message.Contains(item.Id));

        // And the fold learned it. The append is the fallible half; applying to the fold cannot fail
        // for a reason the ledger file caused, and the fold is what `factory ls`, the budget,
        // InFlight() and RequeueOrphans read for the rest of this process. A claim lost here leaves the
        // run holding a lease on an item its own fold does not believe is in flight.
        Assert.Equal(WorkItemState.InProgress, state.Items[item.Id].State);
        Assert.Equal("claimant", state.Items[item.Id].Owner);
    }

    [Fact]
    public void ATransitionSurvivesALedgerThisProcessMayNotWrite()
    {
        var state = FactoryState.Replay([]);
        var item = WorkItem.Create("returned to the queue while the ledger is read-only") with
        {
            State = WorkItemState.InProgress,
            Owner = "claimant"
        };
        state.Apply(new WorkItemFiled(item));

        using var ledger = new UnwritableLedger();
        using var history = new JsonlRunHistory(ledger.LedgerPath);
        var mirror = new LedgerMirroringWorkItemStore(new FakeStore(), history, state, _ => { });

        mirror.Transition(item, WorkItemState.Ready, "requeued");

        // The divergence that outlasts the log line's promise: without this the fold still calls the
        // item in flight while the backlog has it queued, so InFlight() and RequeueOrphans work from a
        // fold that disagrees with beads for the whole run -- and "corrected at the next open" is true
        // of the file and false of this process.
        Assert.Equal(WorkItemState.Ready, state.Items[item.Id].State);
        Assert.DoesNotContain(item.Id, state.InFlight().Select(inFlight => inFlight.Id));
    }

    [Fact]
    public void ALedgerThatReportsItselfClosedIsNotSwallowed()
    {
        var state = FactoryState.Replay([]);
        var item = WorkItem.Create("mirrored after the host was disposed");
        state.Apply(new WorkItemFiled(item));

        var mirror = new LedgerMirroringWorkItemStore(
            new FakeStore(), new NullHistory(new ObjectDisposedException(nameof(IRunHistory))), state, _ => { });

        // Deliberately outside the tolerated set: a closed ledger is a lifecycle bug, not an
        // environment fault, and no reconcile heals it — every later append fails the same way. A
        // tolerance wide enough to swallow this loses the whole audit trail without saying so.
        Assert.Throws<ObjectDisposedException>(() => mirror.Update(item));
    }
}
