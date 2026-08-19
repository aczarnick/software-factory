using Factory.Core;

namespace Factory.Runtime;

/// <summary>
/// Keeps the ledger's audit copy of item state in step with an authoritative backlog store.
///
/// The backlog store is written first and its failure aborts the transition; the ledger append that
/// follows is best-effort, because a lost append is corrected by reconcile at the next open while a
/// lost backlog write would be a wrong backlog. Without this the ledger fold — which
/// <c>factory ls</c>, dependency queries, orphan requeue and the budget all read — would learn
/// nothing until the next open.
/// </summary>
public sealed class LedgerMirroringWorkItemStore(
    IWorkItemStore inner, IRunHistory history, FactoryState state, Action<string> log) : IWorkItemStore
{
    public WorkItem Add(WorkItem item)
    {
        var added = inner.Add(item);
        Mirror(new WorkItemFiled(added));
        return added;
    }

    public WorkItem Update(WorkItem item)
    {
        var updated = inner.Update(item);
        Mirror(new WorkItemUpdated(updated));
        return updated;
    }

    public WorkItem Transition(WorkItem item, WorkItemState to, string? reason)
    {
        var moved = inner.Transition(item, to, reason);
        MirrorChange(moved, item.State, reason);
        return moved;
    }

    public WorkItem? TryClaim(string owner)
    {
        if (inner.TryClaim(owner) is not { } claimed) return null;

        var merged = WithLocalRunState(claimed);
        MirrorChange(merged, StateOf(merged.Id) ?? WorkItemState.Ready, $"claimed by {owner}");
        return merged;
    }

    public void Release(string id, string reason)
    {
        inner.Release(id, reason);

        // A release always returns the item to Ready and drops the claim (BeadMapper.ReleaseArgs),
        // the same Ready-bound write MirrorChange already handles — but inner.Release returns
        // nothing, so the fold's own copy has to stand in for the "after" item MirrorChange is
        // normally handed by the store.
        if (ItemOf(id) is { } known)
            MirrorChange(known with { State = WorkItemState.Ready, Owner = null, UpdatedAt = DateTimeOffset.UtcNow }, known.State, reason);
    }

    public IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan)
    {
        var reclaimed = inner.Reclaim(olderThan).Select(WithLocalRunState).ToList();
        foreach (var item in reclaimed)
            MirrorChange(item, StateOf(item.Id) ?? WorkItemState.InProgress, "reclaimed from a stale lease");

        return reclaimed;
    }

    public WorkItem? Get(string id) => inner.Get(id);
    public IReadOnlyList<WorkItem> All() => inner.All();
    public void Heartbeat(string id) => inner.Heartbeat(id);
    public void Sync() => inner.Sync();

    private WorkItem? ItemOf(string id) => state.Items.GetValueOrDefault(id);
    private WorkItemState? StateOf(string id) => ItemOf(id)?.State;

    // TryClaim and Reclaim are the two calls that hand back an item the store built rather than one
    // the caller passed in, so they are the two that arrive with this checkout's run state blank.
    // Mirroring one of those verbatim would blank the fold's copy — the spend and attempt columns
    // `factory ls` and `factory show` read — and the claim loop then hands the same item to the
    // station, whose run record stamps the attempt number off it.
    private WorkItem WithLocalRunState(WorkItem fromStore) =>
        ItemOf(fromStore.Id) is { } known ? LocalRunState.CarriedInto(fromStore, known) : fromStore;

    // A state change is the more useful audit record because it carries the reason, but the fold
    // can only apply one to an item it already holds — work another machine filed arrives here
    // unknown, and has to be recorded whole. An owner change forces the same whole-record write:
    // FactoryState.ApplyLocked applies a WorkItemStateChanged as State + UpdatedAt only, so a claim
    // or a Ready-bound release that changed who holds the item would otherwise never reach the
    // fold at all, and the next open would find every one of them "reconciled" from the backlog.
    // The cost is the reason string, which is lost on the write that changed the owner but kept on
    // every other one.
    private void MirrorChange(WorkItem after, WorkItemState from, string? reason)
    {
        var known = ItemOf(after.Id);
        Mirror(known is null || known.Owner != after.Owner
            ? new WorkItemUpdated(after)
            : new WorkItemStateChanged(after.Id, from, after.State, reason));
    }

    // The fold is applied even when the append was lost, because only the append is fallible for a
    // reason the environment caused. Wrapping both meant a tolerated append also skipped the fold --
    // and the fold is what `factory ls`, the budget, InFlight() and RequeueOrphans read for the rest
    // of this process, so a Ready-bound transition lost that way left the item listed in flight here
    // while the backlog had it queued. "Corrected at the next open" is true of the ledger file and
    // false of this run.
    private void Mirror(FactoryEvent evt)
    {
        TryAppendToLedger(evt);
        state.Apply(evt);
    }

    // See LedgerFaultTolerance for what this catches and why -- the same predicate
    // BacklogReconciler.TryAppend uses for the identical append, so the two agree by construction
    // rather than by two comments promising they match.
    private void TryAppendToLedger(FactoryEvent evt)
    {
        try
        {
            history.Append(evt);
        }
        catch (Exception ex) when (LedgerFaultTolerance.IsTolerable(ex))
        {
            log($"the audit copy of {Describe(evt)} could not be written to the ledger and will be " +
                $"restored at the next open; the change itself stands: {ex.Message}");
        }
    }

    private static string Describe(FactoryEvent evt) => evt switch
    {
        WorkItemFiled f => f.Item.Id,
        WorkItemUpdated u => u.Item.Id,
        WorkItemStateChanged c => c.ItemId,
        _ => evt.EventId
    };
}
