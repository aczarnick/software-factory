using Factory.Core;

namespace Factory.Runtime;

/// <summary>
/// Backlog stored in the factory's own event ledger. This is the default provider and
/// preserves the behaviour the factory had before the backlog was a port: every write is an
/// event, and current state is the fold over those events.
/// </summary>
public sealed class LedgerWorkItemStore(IRunHistory history, FactoryState state) : IWorkItemStore
{
    private readonly Lock _gate = new();

    public WorkItem Add(WorkItem item)
    {
        Record(new WorkItemFiled(item));
        return item;
    }

    public WorkItem Update(WorkItem item)
    {
        var updated = item with { UpdatedAt = DateTimeOffset.UtcNow };
        Record(new WorkItemUpdated(updated));
        return updated;
    }

    public WorkItem Transition(WorkItem item, WorkItemState to, string? reason)
    {
        if (!WorkItemStates.CanTransition(item.State, to))
            throw new InvalidOperationException(
                $"Illegal transition {item.State} -> {to} for {item.Id}.");

        Record(new WorkItemStateChanged(item.Id, item.State, to, reason));
        return item with { State = to, UpdatedAt = DateTimeOffset.UtcNow };
    }

    public WorkItem? Get(string id) => state.Items.GetValueOrDefault(id);

    public IReadOnlyList<WorkItem> All() => [.. state.Items.Values];

    public WorkItem? TryClaim(string owner)
    {
        lock (_gate)
        {
            if (state.Dispatchable().FirstOrDefault() is not { } next) return null;
            return Transition(next, WorkItemState.InProgress, $"claimed by {owner}");
        }
    }

    /// <summary>No-op: a ledger-backed backlog has no lease to refresh. Beads does, and
    /// implements this in phase 3.</summary>
    public void Heartbeat(string id) { }

    public void Release(string id, string reason)
    {
        if (Get(id) is not { } item) return;
        Transition(item, WorkItemState.Ready, reason);
    }

    /// <summary>No-op: a local ledger has no remote to reconcile with.</summary>
    public void Sync() { }

    /// <summary>Nothing to reclaim: without leases there is no staleness to detect. The
    /// orchestrator's own restart requeue already covers in-process crashes.</summary>
    public IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan) => [];

    private void Record(FactoryEvent evt)
    {
        history.Append(evt);
        state.Apply(evt);
    }
}
