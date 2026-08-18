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
        var corrected = 0;

        foreach (var authoritative in store.All())
        {
            var known = local.GetValueOrDefault(authoritative.Id);
            if (known is not null && SharedState(known) == SharedState(authoritative)) continue;

            FactoryEvent correction = known is null
                ? new WorkItemFiled(authoritative)
                : new WorkItemUpdated(WithLocalRunState(authoritative, known));

            history.Append(correction);
            state.Apply(correction);
            corrected++;
        }

        if (corrected > 0) log($"reconciled {corrected} item(s) from the backlog store");
    }

    /// <summary>Everything the backlog store is authoritative for, in a form that compares by
    /// value. Serialising rather than listing fields means a field added to the mapping is
    /// compared automatically, and it sidesteps records comparing their list members by
    /// reference.</summary>
    private static string SharedState(WorkItem item) => FactoryJson.Write(StripLocalRunState(item));

    // Volatile per-run state belongs to this checkout, not to the shared backlog: the station and
    // worktree are how a blocked item resumes without redoing verified work, and spend is measured
    // from this machine's own ledger. A correction must not blank them.
    private static WorkItem StripLocalRunState(WorkItem item) => item with
    {
        Station = null,
        Worktree = null,
        Attempts = 0,
        LastError = null,
        SpentUsd = 0m,
        UpdatedAt = default
    };

    private static WorkItem WithLocalRunState(WorkItem authoritative, WorkItem local) => authoritative with
    {
        Station = local.Station,
        Worktree = local.Worktree,
        Attempts = local.Attempts,
        LastError = local.LastError,
        SpentUsd = local.SpentUsd,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}
