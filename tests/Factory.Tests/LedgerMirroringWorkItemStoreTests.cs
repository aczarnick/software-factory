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
        public WorkItem Transition(WorkItem item, WorkItemState to, string? reason) => item with { State = to };
        public WorkItem? Get(string id) => null;
        public IReadOnlyList<WorkItem> All() => [];
        public WorkItem? TryClaim(string owner) => ClaimResult;
        public void Heartbeat(string id) { }
        public void Release(string id, string reason) { }
        public void Sync() { }
        public IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan) => ReclaimResult;
    }

    private sealed class NullHistory : IRunHistory
    {
        public void Append(FactoryEvent evt) { }
        public IEnumerable<FactoryEvent> ReadFrom(long afterSeq) => [];
        public IReadOnlyList<RunRecord> RunsForItem(string itemId) => [];
        public IReadOnlyList<RunRecord> RunsForStation(string stationId) => [];
        public SpendTotals Totals() => SpendTotals.Empty;
        public BudgetRestoreView ForBudget() => BudgetRestoreView.Empty;
        public IReadOnlyDictionary<string, string> Champions() => new Dictionary<string, string>();
        public void Dispose() { }
    }

    [Fact]
    public void Claiming_an_item_the_fold_already_holds_carries_the_new_owner_into_it()
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
    public void Releasing_a_claimed_item_clears_its_owner_in_the_fold()
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
    public void Reclaiming_a_stale_lease_keeps_the_run_state_the_backlog_does_not_store()
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
}
