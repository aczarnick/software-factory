using Factory.Core;

namespace Factory.Runtime;

/// <summary>
/// Backlog stored in beads. Authoritative for item state across every machine sharing the Dolt
/// remote; volatile per-run state stays in the local ledger.
/// </summary>
public sealed class BeadsWorkItemStore(BeadsCli cli, string owner) : IWorkItemStore
{
    public WorkItem Add(WorkItem item)
    {
        Write(BeadMapper.CreateArgs(item), $"file {item.Id}");

        // bd has no status flag on create, so anything but Ready needs a second write. The bead is
        // briefly claimable in between; one local write apart, and accepted.
        return item.State == WorkItemState.Ready ? item : Update(item);
    }

    public WorkItem Update(WorkItem item)
    {
        // --actor names this checkout, as every other mutating call does. Load-bearing when a
        // Ready-bound update clears the assignee of a bead still in progress: bd refuses that to
        // anyone but the holder, and with no actor it resolves one from the human's git identity
        // rather than from the checkout that holds the claim — so the factory is refused its own
        // item, and a run cancelled mid-station cannot put it back.
        Write([.. BeadMapper.UpdateArgs(item), "--actor", owner], $"update {item.Id}");
        return item with { UpdatedAt = DateTimeOffset.UtcNow };
    }

    public WorkItem Transition(WorkItem item, WorkItemState to, string? reason)
    {
        if (!WorkItemStates.CanTransition(item.State, to))
            throw new InvalidOperationException(
                $"Illegal transition {item.State} -> {to} for {item.Id}.");

        var moved = item with { State = to, UpdatedAt = DateTimeOffset.UtcNow };
        Update(moved);
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

    public void Release(string id, string reason)
    {
        if (Get(id) is null) return;

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
