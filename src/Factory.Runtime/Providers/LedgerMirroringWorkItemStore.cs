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
        var claimed = inner.TryClaim(owner);
        if (claimed is not null) MirrorChange(claimed, StateOf(claimed.Id) ?? WorkItemState.Ready, $"claimed by {owner}");
        return claimed;
    }

    public void Release(string id, string reason)
    {
        inner.Release(id, reason);
        if (StateOf(id) is { } from) Mirror(new WorkItemStateChanged(id, from, WorkItemState.Ready, reason));
    }

    public IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan)
    {
        var reclaimed = inner.Reclaim(olderThan);
        foreach (var item in reclaimed)
            MirrorChange(item, StateOf(item.Id) ?? WorkItemState.InProgress, "reclaimed from a stale lease");

        return reclaimed;
    }

    public WorkItem? Get(string id) => inner.Get(id);
    public IReadOnlyList<WorkItem> All() => inner.All();
    public void Heartbeat(string id) => inner.Heartbeat(id);
    public void Sync() => inner.Sync();

    private WorkItemState? StateOf(string id) => state.Items.GetValueOrDefault(id)?.State;

    // A state change is the more useful audit record because it carries the reason, but the fold
    // can only apply one to an item it already holds — work another machine filed arrives here
    // unknown, and has to be recorded whole.
    private void MirrorChange(WorkItem after, WorkItemState from, string? reason) =>
        Mirror(StateOf(after.Id) is null
            ? new WorkItemUpdated(after)
            : new WorkItemStateChanged(after.Id, from, after.State, reason));

    private void Mirror(FactoryEvent evt)
    {
        try
        {
            history.Append(evt);
            state.Apply(evt);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            log($"the audit copy of {Describe(evt)} could not be written and will be corrected at the next open: {ex.Message}");
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
