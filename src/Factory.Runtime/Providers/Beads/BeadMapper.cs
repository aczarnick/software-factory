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
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped work item kind.")
    };

    public static WorkItemKind KindFor(string issueType) => issueType switch
    {
        "feature" => WorkItemKind.Feature,
        "bug" => WorkItemKind.Bug,
        "chore" => WorkItemKind.Chore,
        "spike" => WorkItemKind.Spike,
        "refactor" => WorkItemKind.Refactor,
        "improvement" => WorkItemKind.Improvement,
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
        ProvenanceSource = item.Provenance.Source
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
            Provenance = new Provenance(metadata.ProvenanceKind, metadata.ProvenanceSource)
        };
    }

    // Only the beads this one waits on. The self-id guard covers the reversed edge shape, where
    // the same row would otherwise read as an item depending on itself.
    private static IEnumerable<string> Blockers(BeadRecord bead) =>
        bead.Dependencies
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

        if (item.AcceptanceCriteria.Count > 0)
        {
            args.Add("--acceptance");
            args.Add(string.Join("\n", item.AcceptanceCriteria.Select(c => $"- {c.Statement} ({c.Verification.Describe})")));
        }

        foreach (var dependency in item.DependsOn) { args.Add("--deps"); args.Add($"depends-on:{dependency}"); }

        return args;
    }

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

    /// <summary>Reverts this node's stale leases. The grace window is emitted in seconds because a
    /// sub-minute window would truncate to <c>0m</c>.</summary>
    public static IReadOnlyList<string> ReclaimArgs(TimeSpan olderThan, string owner) =>
        ["reclaim", "--older-than", $"{(long)olderThan.TotalSeconds}s", "--json", "--actor", owner];

    public static IReadOnlyList<string> UpdateArgs(WorkItem item) =>
    [
        "update", item.Id,
        "--status", StatusFor(item.State),
        "-p", item.Priority.ToString(),
        "--metadata", MetadataFor(item)
    ];
}
