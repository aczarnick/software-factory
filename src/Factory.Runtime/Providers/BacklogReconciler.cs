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

            if (TryAppend(correction, state, history, log, authoritative.Id)) corrected++;
        }

        foreach (var vanished in local.Keys.Except(seenIds))
            log($"{vanished} is in the fold but no longer exists in the backlog store, and was left as is");

        if (corrected > 0) log($"reconciled {corrected} item(s) from the backlog store");
    }

    // Mirrors LedgerMirroringWorkItemStore.Mirror's tolerance for the identical append, so the two
    // agree deliberately rather than by accident: a backlog write already committed before reconcile
    // ever runs, so a ledger this process cannot write should degrade the factory, not stop it from
    // starting, and the next reconcile repeats the correction. See that decorator's catch for why the
    // set is exactly these two and not wider.
    private static bool TryAppend(
        FactoryEvent correction, FactoryState state, IRunHistory history, Action<string> log, string itemId)
    {
        try
        {
            history.Append(correction);
            state.Apply(correction);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"the correction to {itemId} could not be written to the ledger and will be " +
                $"attempted again at the next open: {ex.Message}");
            return false;
        }
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
