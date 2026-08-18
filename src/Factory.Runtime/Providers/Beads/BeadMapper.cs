using Factory.Core;

namespace Factory.Runtime;

/// <summary>Translates between a <see cref="WorkItem"/> and a bead. Native beads fields are used
/// where they exist; everything structured travels in the bead's metadata JSON.</summary>
public static class BeadMapper
{
    /// <summary>Custom vocabulary this mapping requires. Installed once at deployment. The
    /// <c>frozen</c> category on draft and failed is load-bearing: it keeps proposals and failed
    /// work out of <c>bd ready</c>, preserving the factory's --include-proposed semantics.</summary>
    public const string CustomStatuses =
        "draft:frozen,in_review:wip,verified:wip,failed:frozen,cancelled:done";

    public const string CustomTypes = "refactor,improvement";

    public static string StatusFor(WorkItemState state) => state switch
    {
        WorkItemState.Draft => "draft",
        WorkItemState.Ready => "open",
        WorkItemState.InProgress => "in_progress",
        WorkItemState.InReview => "in_review",
        WorkItemState.Verified => "verified",
        WorkItemState.Done => "closed",
        WorkItemState.Blocked => "blocked",
        WorkItemState.Failed => "failed",
        WorkItemState.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unmapped work item state.")
    };

    public static WorkItemState StateFor(string status) => status switch
    {
        "draft" => WorkItemState.Draft,
        "open" => WorkItemState.Ready,
        "in_progress" => WorkItemState.InProgress,
        "in_review" => WorkItemState.InReview,
        "verified" => WorkItemState.Verified,
        "closed" => WorkItemState.Done,
        "blocked" => WorkItemState.Blocked,
        "failed" => WorkItemState.Failed,
        "cancelled" => WorkItemState.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped bead status.")
    };

    public static string TypeFor(WorkItemKind kind) => kind switch
    {
        WorkItemKind.Feature => "feature",
        WorkItemKind.Bug => "bug",
        WorkItemKind.Chore => "chore",
        WorkItemKind.Spike => "spike",
        WorkItemKind.Refactor => "refactor",
        WorkItemKind.Improvement => "improvement",
        WorkItemKind.Task => "task",
        WorkItemKind.Epic => "epic",
        WorkItemKind.Decision => "decision",
        WorkItemKind.Story => "story",
        WorkItemKind.Milestone => "milestone",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped work item kind.")
    };

    /// <summary>Reads a bead's type. Every type bd has built in maps to a kind of its own, because
    /// <see cref="UpdateArgs"/> writes the mapped kind straight back out: a type read as something
    /// else is a type destroyed on the factory's first update, and <c>task</c> is bd's default, so
    /// that is every bead filed without an explicit <c>-t</c>. Only <c>refactor</c> and
    /// <c>improvement</c> are the factory's own additions (see <see cref="CustomTypes"/>).
    ///
    /// Genuinely unknown custom vocabulary still falls back rather than throwing — a read that threw
    /// would take down every command that lists the backlog — and is still rewritten on the next
    /// update. Carrying the raw value on the item is the only complete fix and is not worth a field
    /// on <see cref="WorkItem"/> for a type nobody has configured.</summary>
    public static WorkItemKind KindFor(string issueType) => issueType switch
    {
        "feature" => WorkItemKind.Feature,
        "bug" => WorkItemKind.Bug,
        "chore" => WorkItemKind.Chore,
        "spike" => WorkItemKind.Spike,
        "refactor" => WorkItemKind.Refactor,
        "improvement" => WorkItemKind.Improvement,
        "task" => WorkItemKind.Task,
        "epic" => WorkItemKind.Epic,
        "decision" => WorkItemKind.Decision,
        "story" => WorkItemKind.Story,
        "milestone" => WorkItemKind.Milestone,
        _ => WorkItemKind.Feature
    };

    /// <summary>Everything beads has no native field for.</summary>
    public static string MetadataFor(WorkItem item) => FactoryJson.Write(new BeadMetadata
    {
        Intent = item.Intent,
        Requirements = item.Requirements,
        Criteria = item.AcceptanceCriteria,
        Assumptions = item.Assumptions,
        Labels = item.Labels,
        ParentId = item.ParentId,
        BudgetUsd = item.BudgetUsd,
        ProvenanceKind = item.Provenance.Kind,
        ProvenanceSource = item.Provenance.Source,
        CreatedAt = item.CreatedAt
    });

    public static WorkItem ToWorkItem(BeadRecord bead)
    {
        var metadata = bead.Metadata is { } element
            ? FactoryJson.Read<BeadMetadata>(element.GetRawText()) ?? new BeadMetadata()
            : new BeadMetadata();

        return new WorkItem
        {
            Id = bead.Id,
            Title = bead.Title,
            Intent = metadata.Intent ?? bead.Description ?? "",
            Kind = KindFor(bead.IssueType),
            State = StateFor(bead.Status),
            Priority = bead.Priority,
            Requirements = metadata.Requirements,
            AcceptanceCriteria = metadata.Criteria,
            Assumptions = metadata.Assumptions,
            Labels = metadata.Labels,
            ParentId = metadata.ParentId,
            DependsOn = [.. Blockers(bead)],
            BudgetUsd = metadata.BudgetUsd,
            Provenance = new Provenance(metadata.ProvenanceKind, metadata.ProvenanceSource),

            // Read from the bead, never from the metadata: beads owns the assignee natively, and a
            // second copy would go stale the moment another machine claimed or released the bead.
            // Empty normalises to null because reconcile compares the whole mapped projection, and
            // "" against null would rewrite every unheld item into the ledger on every open.
            Owner = string.IsNullOrEmpty(bead.Assignee) ? null : bead.Assignee,

            // The filing time the factory recorded, falling back to the bead's own stamp for work
            // some other tool filed. Never defaulted to now: dispatch order breaks priority ties on
            // this, and reconcile compares it, so a fresh value on every read would both reshuffle
            // the queue and make an unchanged backlog look changed on every open.
            CreatedAt = metadata.CreatedAt ?? bead.CreatedAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = bead.UpdatedAt ?? DateTimeOffset.UtcNow
        };
    }

    // Only the beads this one waits on. Non-blocking edge types are dropped rather than read as
    // blockers (see BeadDependency.IsBlocking), and the self-id guard covers the reversed edge
    // shape, where the same row would otherwise read as an item depending on itself.
    private static IEnumerable<string> Blockers(BeadRecord bead) =>
        bead.Dependencies
            .Where(dependency => dependency.IsBlocking)
            .Select(dependency => dependency.BlockerId)
            .Where(id => !string.IsNullOrEmpty(id) && id != bead.Id);

    public static IReadOnlyList<string> CreateArgs(WorkItem item)
    {
        var args = new List<string>
        {
            "create", item.Title,
            "--id", item.Id,
            "-t", TypeFor(item.Kind),
            "-p", item.Priority.ToString(),
            "--metadata", MetadataFor(item),
            "--json"
        };

        if (!string.IsNullOrWhiteSpace(item.Intent)) { args.Add("-d"); args.Add(item.Intent); }

        if (item.AcceptanceCriteria.Count > 0) { args.Add("--acceptance"); args.Add(AcceptanceFor(item)); }

        foreach (var dependency in item.DependsOn) { args.Add("--deps"); args.Add($"depends-on:{dependency}"); }

        return args;
    }

    // The bead's own acceptance_criteria cell, for readers of the backlog that are not the factory;
    // the structured criteria the factory reads back travel in the metadata.
    private static string AcceptanceFor(WorkItem item) =>
        string.Join("\n", item.AcceptanceCriteria.Select(c => $"- {c.Statement} ({c.Verification.Describe})"));

    /// <summary>Reads every bead in every status. <c>--all</c> reaches closed work and
    /// <c>--limit 0</c> defeats the default page size of 50, which would otherwise truncate a
    /// backlog silently.</summary>
    public static IReadOnlyList<string> AllArgs() => ["list", "--all", "--limit", "0", "--json"];

    /// <summary>Reads one bead by id. Uses <c>list --id</c> rather than <c>show</c>: an id beads
    /// does not know exits 0 with an empty array here, where <c>show</c> exits non-zero and would
    /// be indistinguishable from a broken database.</summary>
    public static IReadOnlyList<string> GetArgs(string id) =>
        ["list", "--id", id, "--all", "--limit", "0", "--json"];

    /// <summary>Atomically takes the first ready bead. <c>--actor</c> is the only lever that sets
    /// the assignee — <c>--assignee</c> on <c>ready</c> filters instead of assigning — and the
    /// factory names the checkout explicitly so the value does not fall back to the human's
    /// git identity.</summary>
    public static IReadOnlyList<string> ClaimArgs(string owner) =>
        ["ready", "--claim", "--json", "--actor", owner];

    /// <summary>Returns a bead to the queue. Deliberately not <c>bd unclaim</c>, which only works
    /// while the bead is still <c>in_progress</c> and fails once a station has moved it on; this
    /// form clears status, assignee and lease together from any state.</summary>
    public static IReadOnlyList<string> ReleaseArgs(string id, string owner) =>
        ["update", id, "--status", StatusFor(WorkItemState.Ready), "--assignee", "", "--actor", owner];

    /// <summary>Reverts this node's own stale leases. The grace window is emitted in seconds
    /// because a sub-minute window would truncate to <c>0m</c>. <c>--assignee</c> is bd's scope
    /// filter and is what restricts the reap to leases held by <paramref name="owner"/> — without
    /// it, a stale lease anywhere in the shared store is fair game, including one another machine
    /// is actively working under, because heartbeats are node-local and do not replicate.
    /// <c>--actor</c> stays alongside it: that is the audit-trail flag, unrelated to scope.</summary>
    public static IReadOnlyList<string> ReclaimArgs(TimeSpan olderThan, string owner) =>
        ["reclaim", "--older-than", $"{(long)olderThan.TotalSeconds}s", "--json",
         "--actor", owner, "--assignee", owner];

    /// <summary>Writes an item's mapped fields over the bead. Every field beads owns natively is
    /// sent on every update, not only the ones that changed: reconcile compares the whole mapped
    /// projection and lets beads win, so a field left out is not merely unsaved in beads — the next
    /// open reverts the local edit to match.
    ///
    /// <c>-d</c> is unconditional where <see cref="CreateArgs"/> makes it conditional: bd accepts
    /// <c>-d ""</c> and empties the cell, so an item that has lost its intent can say so, where
    /// omitting the flag would leave beads asserting the old value with nothing to ever correct it.
    /// <c>--acceptance</c> stays conditional for the opposite reason, explained at the flag itself.
    /// An empty <c>--title</c> bd refuses (exit 1) — exactly as it refuses one on create, so no bead
    /// the factory filed can have one, and the write fails loudly rather than keeping a stale title
    /// quietly.
    ///
    /// There is deliberately no <c>--deps</c>: bd <c>update</c> has no such flag, and a post-filing
    /// edge needs <c>bd dep add</c> / <c>bd dep remove</c> instead. Dependency edits therefore do
    /// not reach beads through this path.
    ///
    /// Returning an item to Ready also drops the claim, because <c>bd ready --claim</c> skips an
    /// open bead that still carries an assignee — even for the actor named in it, and
    /// <c>--claim</c> refuses to combine with
    /// <c>--assignee</c> — so an item requeued with its claim intact would be Ready everywhere and
    /// claimable nowhere. Any other status keeps the assignee: clearing it while work is in flight
    /// would hand the item to whichever machine claimed next.
    ///
    /// <c>--actor</c> is always named because <c>bd</c> refuses to clear the assignee of a bead it
    /// believes another actor holds — including the holder's own item, when the write does not name
    /// it — so a Ready-bound write with no actor is refused on exactly the item it is meant to
    /// requeue.</summary>
    public static IReadOnlyList<string> UpdateArgs(WorkItem item, string owner)
    {
        var args = new List<string>
        {
            "update", item.Id,
            "--title", item.Title,
            "-t", TypeFor(item.Kind),
            "--status", StatusFor(item.State),
            "-p", item.Priority.ToString(),
            "-d", item.Intent,
            "--metadata", MetadataFor(item),
            "--actor", owner
        };

        // Sent only when the item has criteria of its own, deliberately unlike -d above. The two
        // differ because their reads differ: Intent falls back to the bead's own description when the
        // factory has no metadata there, so an empty Intent really means the item has nothing to say,
        // while AcceptanceCriteria has no such fallback and arrives empty for every bead another tool
        // filed. An unconditional --acceptance would render that emptiness back over beads' own cell
        // and destroy a human's criteria on the factory's first update. The accepted cost is the
        // reverse case: clearing a factory item's criteria leaves the bead's cell stale, while the
        // metadata blob — the authority for what the factory believes — is correct. Losing another
        // tool's data is worse than a stale human-facing cell.
        if (item.AcceptanceCriteria.Count > 0) { args.Add("--acceptance"); args.Add(AcceptanceFor(item)); }

        if (item.State == WorkItemState.Ready) { args.Add("--assignee"); args.Add(""); }

        return args;
    }

    /// <summary>The second write that gives a freshly created bead its real status. <c>bd create</c>
    /// has no status flag, so filing anything but Ready takes two writes and the bead is claimable
    /// in the window between them. <c>--if-status open</c> makes this write refuse — nothing written,
    /// exit 13 — once the bead has moved on, rather than dragging a bead another machine claimed in
    /// that window back to <c>draft</c> and dropping the lease it is working under, which bd
    /// otherwise does while exiting 0.</summary>
    public static IReadOnlyList<string> FilingStatusArgs(WorkItem item, string owner) =>
        [.. UpdateArgs(item, owner), "--if-status", StatusFor(WorkItemState.Ready)];
}
