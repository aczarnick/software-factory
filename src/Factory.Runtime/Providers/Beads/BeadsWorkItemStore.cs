using Factory.Core;

namespace Factory.Runtime;

/// <summary>
/// Backlog stored in beads. Authoritative for item state across every machine sharing the Dolt
/// remote; volatile per-run state stays in the local ledger.
/// </summary>
public sealed class BeadsWorkItemStore(BeadsCli cli, string owner, Action<string> log) : IWorkItemStore
{
    public WorkItem Add(WorkItem item)
    {
        Write(BeadMapper.CreateArgs(item), $"file {item.Id}");

        // bd create has no status flag, so anything but Ready needs a second write.
        return item.State == WorkItemState.Ready ? item : FinishFiling(item);
    }

    public WorkItem Update(WorkItem item)
    {
        Write(BeadMapper.UpdateArgs(item, owner), $"update {item.Id}");
        ReconcileDependencies(item);

        // A Ready-bound write just cleared the bead's assignee (BeadMapper.UpdateArgs), so the
        // returned item has to say the same thing — otherwise the mirror carries a stale Owner
        // into the ledger for a bead that bd now shows as unassigned.
        return item.State == WorkItemState.Ready
            ? item with { Owner = null, UpdatedAt = DateTimeOffset.UtcNow }
            : item with { UpdatedAt = DateTimeOffset.UtcNow };
    }

    public WorkItem Transition(WorkItem item, WorkItemState to, string? reason)
    {
        if (!WorkItemStates.CanTransition(item.State, to))
            throw new InvalidOperationException(
                $"Illegal transition {item.State} -> {to} for {item.Id}.");

        var moved = Update(item with { State = to, UpdatedAt = DateTimeOffset.UtcNow });
        Note(item.Id, reason);

        return moved;
    }

    public WorkItem? Get(string id) =>
        cli.Json<BeadRecord>([.. BeadMapper.GetArgs(id)]).Select(BeadMapper.ToWorkItem).FirstOrDefault();

    public IReadOnlyList<WorkItem> All() =>
        [.. cli.Json<BeadRecord>([.. BeadMapper.AllArgs()]).Select(BeadMapper.ToWorkItem)];

    public WorkItem? TryClaim(string claimant) =>
        cli.Json<BeadRecord>([.. BeadMapper.ClaimArgs(claimant)])
           .Select(BeadMapper.ToWorkItem)
           .FirstOrDefault();

    /// <summary>Pushes the lease out. Best-effort by design: beads refuses a heartbeat on a bead
    /// this node does not hold in progress, and a station that has moved its item to review has no
    /// lease left to refresh. Neither is a backlog failure, so neither halts the factory.</summary>
    public void Heartbeat(string id) => cli.Exec("heartbeat", id, "--actor", owner);

    /// <summary>Returns an item to the queue. Refuses one whose current state cannot reach Ready,
    /// which the port requires and <see cref="LedgerWorkItemStore"/> gets from routing through
    /// <c>Transition</c>: <c>bd update --status open</c> reopens even a closed bead and exits 0, so
    /// without the check a release of integrated work would put it back in <c>bd ready</c> for the
    /// next claim to pick up.</summary>
    public void Release(string id, string reason)
    {
        if (Get(id) is not { } item) return;

        if (!WorkItemStates.CanTransition(item.State, WorkItemState.Ready))
            throw new InvalidOperationException(
                $"Illegal transition {item.State} -> {WorkItemState.Ready} for {id}.");

        Write(BeadMapper.ReleaseArgs(id, owner), $"release {id}");
        Note(id, reason);
    }

    /// <summary>Best-effort: beads is a replica model, so an unreachable or unconfigured remote
    /// leaves the local database complete and the factory able to work on.</summary>
    public void Sync() => cli.Exec("sync");

    public IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan)
    {
        var response = cli.JsonObject<BeadsReclaimResponse>([.. BeadMapper.ReclaimArgs(olderThan, owner)]);

        return
        [
            .. (response?.Reclaimed ?? [])
                .Select(lease => Get(lease.Id))
                .OfType<WorkItem>()
        ];
    }

    /// <summary>bd's exit code for a write whose <c>--if-status</c> or <c>--if-assignee</c>
    /// precondition no longer held: nothing was written, and another actor won the race. Any other
    /// non-zero code is a genuine failure and must not be read as a lost race.</summary>
    private const int PreconditionNoLongerHeld = 13;

    // The second of Add's two writes. Losing the race is not a backlog failure: throwing would let
    // GuardedWorkItemStore halt the whole factory because another machine claimed a proposal this
    // one had just filed. So the collision is reported and the bead is re-read, because the item
    // handed back is what the fold records — returning the Draft this checkout intended would put an
    // item in the fold that beads says is in progress somewhere else.
    private WorkItem FinishFiling(WorkItem item)
    {
        var result = cli.Exec([.. BeadMapper.FilingStatusArgs(item, owner)]);
        if (result.Ok) return item with { UpdatedAt = DateTimeOffset.UtcNow };

        if (result.ExitCode != PreconditionNoLongerHeld)
            throw new InvalidOperationException(
                $"Could not file {item.Id} as {item.State} in beads: {result.Combined}");

        log($"{item.Id} was claimed by another checkout while being filed, so it stays as beads " +
            "reports it rather than being forced back to a proposal");

        return Get(item.Id) ?? item;
    }

    /// <summary>Makes the bead's blocking edges match the item's. <c>bd update</c> has no
    /// <c>--deps</c> flag, so edges are a separate mechanism from every other field: without this an
    /// edge added after filing never reaches beads, and — because reconcile compares the whole mapped
    /// projection and lets beads win — the next open reverts the edit locally too. An edge dropped
    /// locally survives in beads the same way, holding work back everywhere.</summary>
    ///
    /// <remarks>The read costs one extra <c>bd</c> invocation per update. It cannot be avoided:
    /// <c>bd update --json</c> returns the bead without its <c>dependencies</c> array, and the only
    /// other candidate — comparing against the caller's own previous copy of the item — is exactly
    /// the local-authority assumption this method exists to remove. The writes are skipped entirely
    /// when the two agree, which is every update that is not an edge edit.
    ///
    /// Driving both halves from <see cref="WorkItem.DependsOn"/> is what keeps another tool's
    /// <em>non-blocking</em> edges safe, and only those. That projection carries just the edge types
    /// beads itself treats as blocking, so a <c>related</c> or <c>parent-child</c> edge a human filed
    /// is never in either set and is never named to <c>bd dep remove</c> — which has no <c>--type</c>
    /// flag and would delete it.
    ///
    /// A foreign <em>blocking</em> edge is not protected, and cannot be by this shape. The item is a
    /// snapshot with no base to diff against, so a blocking edge another actor added after this
    /// checkout read the item is indistinguishable from one this checkout dropped, and is removed as
    /// a local removal. The window is the whole time an item is in flight. Telling the two apart needs
    /// a base revision the port cannot express, so the removals are logged instead: deleting another
    /// actor's row from a shared database must at least leave a trace.</remarks>
    private void ReconcileDependencies(WorkItem item)
    {
        // Read after the field write, not before: an id beads does not know has already failed loudly
        // there, so a null here would mean the bead vanished mid-update and there is nothing to diff.
        if (Get(item.Id) is not { } stored) return;

        foreach (var blocker in item.DependsOn.Except(stored.DependsOn))
            Write(BeadMapper.DependencyAddArgs(item.Id, blocker, owner),
                  $"add the dependency {item.Id} -> {blocker}");

        foreach (var blocker in stored.DependsOn.Except(item.DependsOn))
        {
            Write(BeadMapper.DependencyRemoveArgs(item.Id, blocker, owner),
                  $"remove the dependency {item.Id} -> {blocker}");

            // Logged, never silent: the edge may have been another actor's, and this is the only
            // record that it existed. See the remark above on why the two cases cannot be told apart.
            log($"removed the dependency {item.Id} -> {blocker} from beads");
        }
    }

    private void Write(IReadOnlyList<string> args, string what)
    {
        var result = cli.Exec([.. args]);
        if (!result.Ok)
            throw new InvalidOperationException($"Could not {what} in beads: {result.Combined}");
    }

    // The reason is not authoritative state -- the transition it explains already committed -- so a
    // failure here must not throw. It must not vanish in silence either: without a log line, the
    // ledger records a reason beads never got, and nothing says the two disagree.
    private void Note(string id, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return;

        var result = cli.Exec("note", id, reason, "--actor", owner);
        if (!result.Ok)
            log($"the reason for {id}'s transition ('{reason}') could not be recorded in beads: {result.Combined}");
    }
}
