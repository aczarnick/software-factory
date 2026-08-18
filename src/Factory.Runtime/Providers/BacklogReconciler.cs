using Factory.Core;

namespace Factory.Runtime;

/// <summary>
/// Makes the local ledger agree with the authoritative backlog. Corrections only ever flow one
/// way, so a ledger write that failed mid-transition self-heals at the next open.
/// </summary>
public static class BacklogReconciler
{
    public static void Reconcile(
        IWorkItemStore store, FactoryState state, IRunHistory history, Action<string> log)
    {
        var local = state.Items;
        var seenIds = new HashSet<string>();
        var corrected = 0;

        foreach (var authoritative in store.All())
        {
            seenIds.Add(authoritative.Id);
            var known = local.GetValueOrDefault(authoritative.Id);
            if (known is not null && SharedState(known) == SharedState(authoritative)) continue;

            FactoryEvent correction = known is null
                ? new WorkItemFiled(authoritative)
                : new WorkItemUpdated(
                    LocalRunState.CarriedInto(authoritative, known) with { UpdatedAt = DateTimeOffset.UtcNow });

            history.Append(correction);
            state.Apply(correction);
            corrected++;
        }

        // Beads is authoritative for existence too (spec D1): a bead this fold still remembers but
        // that store.All() no longer returns at all was deleted, not merely closed (bd list --all
        // --limit 0 still returns closed beads). Reported and left alone rather than tombstoned:
        // that decision belongs to the sync-gate plan, not to a minors bundle.
        foreach (var vanished in local.Keys.Except(seenIds))
            log($"{vanished} is in the fold but no longer exists in the backlog store, and was left as is");

        if (corrected > 0) log($"reconciled {corrected} item(s) from the backlog store");
    }

    /// <summary>Everything the backlog store is authoritative for, in a form that compares by
    /// value. Serialising rather than listing fields means a field added to the mapping is
    /// compared automatically, and it sidesteps records comparing their list members by
    /// reference. <see cref="WorkItem.UpdatedAt"/> is dropped alongside the local run state
    /// because the store restamps it on every write, so comparing it would report every item as
    /// diverged.</summary>
    private static string SharedState(WorkItem item) =>
        FactoryJson.Write(LocalRunState.Cleared(item) with { UpdatedAt = default });
}
