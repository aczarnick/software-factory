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

        ReportVanished(local.Keys.Except(seenIds).ToList(), log);

        if (corrected > 0) log($"reconciled {corrected} item(s) from the backlog store");
    }

    /// <summary>Above this many vanished ids in one pass, name only the first few and collapse the
    /// rest into a count. A pass with more than this either means a healthy backlog lost one bead,
    /// which is rare enough that naming every one of a handful is useful, or it means the whole
    /// backlog was swapped out from under an unchanged fold -- a recreated database, or a switch of
    /// providers -- where naming hundreds of ids one line each would bury the report it is part of.</summary>
    private const int MaxNamedVanishedItems = 5;

    private static void ReportVanished(IReadOnlyList<string> vanishedIds, Action<string> log)
    {
        foreach (var vanished in vanishedIds.Take(MaxNamedVanishedItems))
            log($"{vanished} is in the fold but no longer exists in the backlog store, so it still " +
                "shows in `factory ls` and still counts as an unmet dependency for anything blocked on it");

        var remaining = vanishedIds.Count - MaxNamedVanishedItems;
        if (remaining > 0)
            log($"...and {remaining} more item(s) in the fold no longer exist in the backlog store");
    }

    // See LedgerFaultTolerance for what this catches and why -- the same predicate
    // LedgerMirroringWorkItemStore.Mirror uses for the identical append, so the two agree by
    // construction rather than by two comments promising they match.
    private static bool TryAppend(
        FactoryEvent correction, FactoryState state, IRunHistory history, Action<string> log, string itemId)
    {
        try
        {
            history.Append(correction);
            state.Apply(correction);
            return true;
        }
        catch (Exception ex) when (LedgerFaultTolerance.IsTolerable(ex))
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
