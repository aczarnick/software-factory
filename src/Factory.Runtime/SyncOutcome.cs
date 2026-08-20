namespace Factory.Runtime;

/// <summary>Result of one attempt to bring an item's worktree up to date with mainline.</summary>
public enum SyncOutcome
{
    /// <summary>Mainline has not advanced past the branch's recorded base commit; nothing was merged.</summary>
    NoOp,

    /// <summary>Mainline had advanced, and the merge into the worktree completed with no conflicts.</summary>
    Synced,

    /// <summary>Mainline had advanced, but merging it into the worktree conflicted; the merge was
    /// aborted and the worktree was left exactly as it was.</summary>
    Conflict
}
