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
        public WorkItem Add(WorkItem item) => item;
        public WorkItem Update(WorkItem item) => item;
        public WorkItem Transition(WorkItem item, WorkItemState to, string? reason) => item with { State = to };
        public WorkItem? Get(string id) => null;
        public IReadOnlyList<WorkItem> All() => [];
        public WorkItem? TryClaim(string owner) => ClaimResult;
        public void Heartbeat(string id) { }
        public void Release(string id, string reason) { }
        public void Sync() { }
        public IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan) => [];
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
}
