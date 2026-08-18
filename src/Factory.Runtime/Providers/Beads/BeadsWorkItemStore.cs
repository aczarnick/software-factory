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

    private void Write(IReadOnlyList<string> args, string what)
    {
        var result = cli.Exec([.. args]);
        if (!result.Ok)
            throw new InvalidOperationException($"Could not {what} in beads: {result.Combined}");
    }

    private void Note(string id, string? reason)
    {
        if (!string.IsNullOrWhiteSpace(reason)) cli.Exec("note", id, reason, "--actor", owner);
    }
}
