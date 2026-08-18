using Factory.Core;

namespace Factory.Runtime;

/// <summary>
/// The item fields this checkout owns rather than the backlog store. The station and worktree are
/// how a blocked item resumes without redoing verified work, attempts and the last error are what a
/// retry is measured and explained by, and spend is measured from this machine's own ledger.
///
/// A backlog store keeps none of them, so every item one hands back carries them blank — and that
/// is the whole rule here: an item arriving from the store must never be allowed to erase them.
/// Both paths that take an item from the store apply it, the reconciler over a correction and the
/// mirroring store over a claim or a reclaim.
///
/// Membership of the set is defined once, in <see cref="CarriedInto"/>, because its correctness
/// rests entirely on being complete: a local field left out of it is a field the next store read
/// silently zeroes.
/// </summary>
internal static class LocalRunState
{
    // WorkItem's own defaults, so Cleared does not restate the field list to blank it.
    private static readonly WorkItem Blank = new() { Id = "", Title = "" };

    /// <summary>Puts <paramref name="local"/>'s run state onto the item the store returned,
    /// leaving everything the store is authoritative for — state and owner included — as the
    /// store reported it.</summary>
    public static WorkItem CarriedInto(WorkItem fromStore, WorkItem local) => fromStore with
    {
        Station = local.Station,
        Worktree = local.Worktree,
        Attempts = local.Attempts,
        LastError = local.LastError,
        SpentUsd = local.SpentUsd
    };

    /// <summary>The item with its run state blanked, for comparing two copies on what the backlog
    /// store is actually authoritative for.</summary>
    public static WorkItem Cleared(WorkItem item) => CarriedInto(item, Blank);
}
