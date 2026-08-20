using System.Text.Json;
using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

public class BeadMapperTests
{
    private static BeadRecord BeadWith(WorkItem item) => new()
    {
        Id = item.Id,
        Title = item.Title,
        Status = BeadMapper.StatusFor(item.State),
        IssueType = BeadMapper.TypeFor(item.Kind),
        Priority = item.Priority,
        Metadata = Element(BeadMapper.MetadataFor(item))
    };

    // bd hands back metadata as a JSON object; MetadataFor produces the string `bd --metadata`
    // takes, so a test standing in for bd has to parse it. Clone detaches it from the document.
    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void EveryWorkItemStateMapsToAStatusAndBack()
    {
        foreach (var state in Enum.GetValues<WorkItemState>())
            Assert.Equal(state, BeadMapper.StateFor(BeadMapper.StatusFor(state)));
    }

    [Fact]
    public void EveryWorkItemKindMapsToATypeAndBack()
    {
        foreach (var kind in Enum.GetValues<WorkItemKind>())
            Assert.Equal(kind, BeadMapper.KindFor(BeadMapper.TypeFor(kind)));
    }

    [Fact]
    public void EveryTypeBeadsHasBuiltInRoundTripsRatherThanFlattening()
    {
        // `bd create --help` lists these nine as bd's built-in types, and `task` is its default — so
        // it is the type on every bead filed without an explicit -t, this repository's own captured
        // fixtures included. Anything KindFor cannot name, TypeFor writes back out as something else.
        string[] builtIn = ["bug", "feature", "task", "epic", "chore", "decision", "spike", "story", "milestone"];

        foreach (var issueType in builtIn)
            Assert.Equal(issueType, BeadMapper.TypeFor(BeadMapper.KindFor(issueType)));
    }

    [Fact]
    public void ACustomTypeNoOneMappedFallsBackInsteadOfThrowing()
    {
        // The fallback stays for genuinely unknown custom vocabulary: a read that threw would take
        // down every command that lists the backlog. Such a type is still rewritten on the next
        // update, which is the residual cost of not carrying the raw value on the item.
        Assert.Equal(WorkItemKind.Feature, BeadMapper.KindFor("a-type-nobody-mapped"));
    }

    [Fact]
    public void CreateArgsCarryTheExplicitIdAndNativeFields()
    {
        var item = WorkItem.Create("add a flag", "users want it", WorkItemKind.Feature) with
        {
            Priority = 1
        };

        var args = BeadMapper.CreateArgs(item);

        Assert.Contains("--id", args);
        Assert.Contains(item.Id, args);
        Assert.Contains("feature", args);
        Assert.Contains("1", args);
    }

    [Fact]
    public void CreateArgsDeclareEveryDependencyAsABlocker()
    {
        var item = WorkItem.Create("second") with { DependsOn = ["wi-aaaa11112222", "wi-bbbb11112222"] };

        var args = BeadMapper.CreateArgs(item);

        Assert.Contains("depends-on:wi-aaaa11112222", args);
        Assert.Contains("depends-on:wi-bbbb11112222", args);
    }

    [Fact]
    public void StructuredCriteriaSurviveTheMetadataRoundTrip()
    {
        var item = WorkItem.Create("thing") with
        {
            AcceptanceCriteria =
            [
                AcceptanceCriterion.Command("cli runs", "dotnet run -- --help"),
                AcceptanceCriterion.Judged("reads well", "prose is clear")
            ],
            BudgetUsd = 1.25m,
            Provenance = Provenance.FromAgent("review")
        };

        var restored = BeadMapper.ToWorkItem(BeadWith(item));

        Assert.IsType<CommandVerification>(restored.AcceptanceCriteria[0].Verification);
        Assert.IsType<AgentJudgeVerification>(restored.AcceptanceCriteria[1].Verification);
        Assert.Equal(1.25m, restored.BudgetUsd);
        Assert.Equal(ProvenanceKind.Agent, restored.Provenance.Kind);
        Assert.Equal("review", restored.Provenance.Source);
    }

    [Fact]
    public void TheStructuredRemainderSurvivesTheMetadataRoundTrip()
    {
        var item = WorkItem.Create("thing", "the underlying goal") with
        {
            Requirements = ["must work"],
            Assumptions = ["the network is up"],
            Labels = ["infra"],
            ParentId = "wi-parent000000"
        };

        var restored = BeadMapper.ToWorkItem(BeadWith(item));

        Assert.Equal("the underlying goal", restored.Intent);
        Assert.Equal(["must work"], restored.Requirements);
        Assert.Equal(["the network is up"], restored.Assumptions);
        Assert.Equal(["infra"], restored.Labels);
        Assert.Equal("wi-parent000000", restored.ParentId);
    }

    [Fact]
    public void NativeFieldsAreReadFromTheBeadRatherThanItsMetadata()
    {
        var item = WorkItem.Create("titled", kind: WorkItemKind.Bug) with
        {
            State = WorkItemState.InReview,
            Priority = 0
        };

        var restored = BeadMapper.ToWorkItem(BeadWith(item));

        Assert.Equal("titled", restored.Title);
        Assert.Equal(WorkItemKind.Bug, restored.Kind);
        Assert.Equal(WorkItemState.InReview, restored.State);
        Assert.Equal(0, restored.Priority);
    }

    [Fact]
    public void BlockersAreRestoredAsTheDependenciesOfTheItemTheyBlock()
    {
        var bead = new BeadRecord
        {
            Id = "wi-bbbb11112222",
            Title = "second",
            Dependencies =
            [
                new BeadDependency { Id = "wi-aaaa11112222", DependencyType = "blocks" }
            ]
        };

        var restored = BeadMapper.ToWorkItem(bead);

        Assert.Equal(["wi-aaaa11112222"], restored.DependsOn);
    }

    [Fact]
    public void ABeadWithNoMetadataStillMaps()
    {
        var restored = BeadMapper.ToWorkItem(new BeadRecord { Id = "wi-cccc11112222", Title = "bare" });

        Assert.Equal("bare", restored.Title);
        Assert.Empty(restored.AcceptanceCriteria);
        Assert.Empty(restored.DependsOn);
    }

    /// <summary>Every key <see cref="BeadMapper.MetadataFor"/> is allowed to write, for an item with
    /// nothing left null. Asserted as a set rather than as a list of forbidden names: naming what
    /// must be absent cannot fail while the field does not exist on <see cref="BeadMetadata"/> at all,
    /// so it never reddens for anything, where the exact set reddens the moment a key appears.</summary>
    private static readonly string[] MetadataKeys =
    [
        "assumptions", "budgetUsd", "createdAt", "criteria", "intent",
        "labels", "parentId", "provenanceKind", "provenanceSource", "requirements"
    ];

    [Fact]
    public void TheMetadataBlobCarriesExactlyTheFieldsBeadsHasNoNativeHomeFor()
    {
        // Two things must not reach it, for two different reasons. Volatile run state — station,
        // worktree, attempts, spend — belongs to the local ledger, and a copy in a shared backlog
        // would let one machine's progress overwrite another's. Owner must not be copied either: beads
        // owns the assignee natively, so a second copy goes stale the moment any machine claims or
        // releases the bead.
        var item = WorkItem.Create("thing", "the intent") with
        {
            Station = "implement",
            Worktree = "/tmp/wt",
            Attempts = 3,
            SpentUsd = 0.42m,
            Owner = "other-machine",
            Requirements = ["works"],
            AcceptanceCriteria = [AcceptanceCriterion.Command("runs", "dotnet run")],
            Assumptions = ["nothing else changed"],
            Labels = ["lane-a"],
            ParentId = "wi-aaaa11112222",
            BudgetUsd = 5m,
            Provenance = Provenance.FromAgent("intake")
        };

        var metadata = BeadMapper.MetadataFor(item);
        var written = JsonDocument.Parse(metadata).RootElement
            .EnumerateObject().Select(property => property.Name).Order();

        Assert.Equal(MetadataKeys, written);

        // The key set cannot see a value smuggled into a key that already belongs here — a
        // `Labels = [.. item.Labels, item.Station]` keeps the set intact — so the two volatile fields
        // with a distinctive string form are named on their values as well. Attempts and spend are
        // left out on purpose: as raw numbers they are indistinguishable from a priority or a budget.
        Assert.DoesNotContain(item.Station!, metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(item.Worktree!, metadata, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NothingTheMetadataBlobCarriesIsLostOnTheWayBack()
    {
        var item = WorkItem.Create("thing", "the intent") with
        {
            Requirements = ["works"],
            Assumptions = ["nothing else changed"],
            Labels = ["lane-a"],
            ParentId = "wi-aaaa11112222",
            BudgetUsd = 5m,
            Provenance = Provenance.FromAgent("intake")
        };

        // The set assertion above catches a key that appears; this catches one that stops being
        // written, which leaves the key in place holding a default and so is invisible to it.
        var restored = BeadMapper.ToWorkItem(BeadWith(item));

        Assert.Equal("the intent", restored.Intent);
        Assert.Equal(["works"], restored.Requirements);
        Assert.Equal(["nothing else changed"], restored.Assumptions);
        Assert.Equal(["lane-a"], restored.Labels);
        Assert.Equal("wi-aaaa11112222", restored.ParentId);
        Assert.Equal(5m, restored.BudgetUsd);
        Assert.Equal(new Provenance(ProvenanceKind.Agent, "intake"), restored.Provenance);
        Assert.Equal(item.CreatedAt, restored.CreatedAt);
    }

    [Fact]
    public void TheAssigneeIsReadAsTheCheckoutHoldingTheItem()
    {
        var bead = new BeadRecord { Id = "wi-eeee11112222", Title = "held", Assignee = "other-machine" };

        // Who holds an item is what lets a requeue tell its own orphan from work another checkout
        // is still running, which bd refuses to let this one release.
        Assert.Equal("other-machine", BeadMapper.ToWorkItem(bead).Owner);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnUnassignedBeadIsOwnedByNobody(string? assignee)
    {
        var bead = new BeadRecord { Id = "wi-eeee11112222", Title = "unheld", Assignee = assignee };

        // Empty and absent have to land on the same value: reconcile compares the whole mapped
        // projection, so "" against null would rewrite the item into the ledger on every open.
        Assert.Null(BeadMapper.ToWorkItem(bead).Owner);
    }

    [Fact]
    public void RefactorAndImprovementMapToCustomTypes()
    {
        Assert.Equal("refactor", BeadMapper.TypeFor(WorkItemKind.Refactor));
        Assert.Equal("improvement", BeadMapper.TypeFor(WorkItemKind.Improvement));
    }

    [Fact]
    public void UpdateArgsCarryTheMappedStatus()
    {
        var item = WorkItem.Create("thing") with { State = WorkItemState.Verified };

        var args = BeadMapper.UpdateArgs(item, "node-a");

        Assert.Contains("--status", args);
        Assert.Contains("verified", args);
    }

    [Fact]
    public void EveryStatusBeadsHasBuiltInReadsAsAStateRatherThanThrowing()
    {
        // bd's own refusal enumerates the set: `invalid status "tombstone" (built-in: open,
        // in_progress, blocked, deferred, closed, pinned, hooked ...)`. The factory maps four of the
        // seven; each of the other three is settable by a human in one command that exits 0 — probed
        // against 1.2.1 — and All() maps every bead in the database, so a read that threw on one
        // would take down every command that opens the host, on every machine sharing the backlog.
        string[] beadsOwnStatuses = ["deferred", "pinned", "hooked"];

        foreach (var status in beadsOwnStatuses)
            Assert.Equal(WorkItemState.Blocked, BeadMapper.StateFor(status));
    }

    [Fact]
    public void ACustomStatusNoOneMappedFallsBackRatherThanThrowing()
    {
        Assert.Equal(WorkItemState.Blocked, BeadMapper.StateFor("a-status-nobody-mapped"));
    }

    [Fact]
    public void EveryStatusTheFactoryWritesIsOneItOwns()
    {
        // The guard that keeps StateFor's list and UnownedStatus' answer from drifting: a status the
        // factory writes but reports as unowned would have its own --status suppressed for ever.
        foreach (var state in Enum.GetValues<WorkItemState>())
            Assert.Null(BeadMapper.UnownedStatus(BeadMapper.StatusFor(state)));
    }

    [Fact]
    public void AStatusTheFactoryDoesNotOwnIsCarriedOnTheItemVerbatim()
    {
        var item = ToWorkItemWithStatus("pinned");

        // Blocked is only how the factory reads it. The bead's own word is what makes the read
        // reversible, so the next update can leave the cell alone instead of guessing.
        Assert.Equal(WorkItemState.Blocked, item.State);
        Assert.Equal("pinned", item.StoreStatus);
    }

    [Fact]
    public void AStatusTheFactoryDoesOwnCarriesNothing()
    {
        Assert.Null(ToWorkItemWithStatus("blocked").StoreStatus);
    }

    private static WorkItem ToWorkItemWithStatus(string status) =>
        BeadMapper.ToWorkItem(new BeadRecord
        {
            Id = "wi-000000000001",
            Title = "a bead a human moved",
            Status = status,
            IssueType = "task",
            Priority = Priorities.Default
        });

    // Captured verbatim from `bd show --json` (beads 1.2.1) against a throwaway database, so the
    // JSON property names are exercised against what beads really emits rather than against a
    // hand-built object that would agree with any misspelling.
    private const string RealBeadJson = """
        {"id": "wi-dddd11112222", "title": "rich bead", "description": "why we want it",
         "acceptance_criteria": "- cli runs", "status": "in_review", "priority": 1,
         "issue_type": "refactor", "created_at": "2026-08-18T13:27:50Z", "created_by": "aczarnick",
         "updated_at": "2026-08-18T13:27:50Z",
         "metadata": {"intent": "why we want it", "labels": ["infra"],
           "criteria": [{"id": "ac-1", "statement": "cli runs",
             "verification": {"kind": "command", "command": "dotnet run", "expectExitCode": 0,
                              "timeoutSeconds": 300}}],
           "budgetUsd": 1.25, "requirements": ["must work"],
           "provenanceKind": "Agent", "provenanceSource": "review"},
         "dependent_count": 0, "dependency_count": 0, "comment_count": 0,
         "revision": -8189281253539385782}
        """;

    // bd reports dependencies in two different shapes. Captured verbatim from `bd show --json`,
    // which embeds the blocking issue:
    private const string RealBlockedBeadFromShow = """
        {"id": "wi-bbbb11112222", "title": "second", "status": "open", "priority": 2,
         "issue_type": "task", "created_at": "2026-08-18T12:54:26Z", "created_by": "aczarnick",
         "updated_at": "2026-08-18T12:54:26Z",
         "dependencies": [{"id": "wi-aaaa11112222", "title": "build a thing", "status": "open",
                           "dependency_type": "blocks"}],
         "dependent_count": 0, "dependency_count": 1, "comment_count": 0,
         "revision": -7237306086704023765}
        """;

    // ...and from `bd list --all --limit 0 --json`, which reports the edge instead:
    private const string RealBlockedBeadFromList = """
        {"id": "wi-bbbb11112222", "title": "second", "status": "open", "priority": 2,
         "issue_type": "task", "created_at": "2026-08-18T12:54:26Z", "created_by": "aczarnick",
         "updated_at": "2026-08-18T12:54:26Z",
         "dependencies": [{"issue_id": "wi-bbbb11112222", "depends_on_id": "wi-aaaa11112222",
                           "type": "blocks", "created_at": "2026-08-18T12:54:26Z",
                           "created_by": "aczarnick", "metadata": "{}"}],
         "dependency_count": 1, "dependent_count": 0, "comment_count": 0}
        """;

    [Fact]
    public void OutputBeadsReallyEmitsDeserialisesIntoEveryMappedField()
    {
        var bead = FactoryJson.Read<BeadRecord>(RealBeadJson)!;

        Assert.Equal("wi-dddd11112222", bead.Id);
        Assert.Equal("rich bead", bead.Title);
        Assert.Equal("why we want it", bead.Description);
        Assert.Equal("in_review", bead.Status);
        Assert.Equal("refactor", bead.IssueType);
        Assert.Equal(1, bead.Priority);
        Assert.Equal("- cli runs", bead.AcceptanceCriteria);
        Assert.Equal(-8189281253539385782L, bead.Revision);
        Assert.Equal(DateTimeOffset.Parse("2026-08-18T13:27:50Z"), bead.CreatedAt);

        var item = BeadMapper.ToWorkItem(bead);

        Assert.Equal(WorkItemKind.Refactor, item.Kind);
        Assert.Equal(WorkItemState.InReview, item.State);
        Assert.Equal("why we want it", item.Intent);
        Assert.Equal(["must work"], item.Requirements);
        Assert.Equal(["infra"], item.Labels);
        Assert.Equal(1.25m, item.BudgetUsd);
        Assert.Equal(ProvenanceKind.Agent, item.Provenance.Kind);
        Assert.Equal("review", item.Provenance.Source);
        Assert.IsType<CommandVerification>(Assert.Single(item.AcceptanceCriteria).Verification);

        // Dispatch order breaks priority ties on CreatedAt, so it has to come from the bead.
        Assert.Equal(DateTimeOffset.Parse("2026-08-18T13:27:50Z"), item.CreatedAt);
    }

    [Theory]
    [InlineData(nameof(RealBlockedBeadFromShow))]
    [InlineData(nameof(RealBlockedBeadFromList))]
    public void BlockersBeadsReallyEmitsBecomeTheItemsDependencies(string fixture)
    {
        // `show` embeds the blocking issue and `list` reports the edge, so reading only one of the
        // two shapes loses every dependency edge from whichever command was not sampled.
        var json = fixture == nameof(RealBlockedBeadFromShow) ? RealBlockedBeadFromShow : RealBlockedBeadFromList;

        var item = BeadMapper.ToWorkItem(FactoryJson.Read<BeadRecord>(json)!);

        Assert.Equal(["wi-aaaa11112222"], item.DependsOn);
    }

    [Theory]
    [InlineData("tracks")]
    [InlineData("related")]
    [InlineData("parent-child")]
    [InlineData("discovered-from")]
    [InlineData("until")]
    [InlineData("caused-by")]
    [InlineData("validates")]
    [InlineData("relates-to")]
    [InlineData("supersedes")]
    public void AnEdgeBeadsDoesNotTreatAsBlockingIsNotReadAsABlocker(string type)
    {
        // Probed against bd 1.2.1: of its ten edge types only `blocks` withholds the dependent from
        // `bd ready`, and only `blocks` counts towards the dependent's own dependency_count. Reading
        // every edge as blocking turns an edge another tool filed as context into a false blocker
        // that FactoryState.Dispatchable() will never dispatch past.
        var fromList = new BeadRecord
        {
            Id = "wi-bbbb11112222",
            Title = "dependent",
            Dependencies =
            [
                new BeadDependency
                {
                    IssueId = "wi-bbbb11112222",
                    DependsOnId = "wi-aaaa11112222",
                    Type = type
                }
            ]
        };

        var fromShow = new BeadRecord
        {
            Id = "wi-bbbb11112222",
            Title = "dependent",
            Dependencies = [new BeadDependency { Id = "wi-aaaa11112222", DependencyType = type }]
        };

        // Both shapes, because `list` reports the edge and `show` embeds the blocking issue.
        Assert.Empty(BeadMapper.ToWorkItem(fromList).DependsOn);
        Assert.Empty(BeadMapper.ToWorkItem(fromShow).DependsOn);
    }

    [Fact]
    public void AnEdgeWithNoTypeRecordedStillBlocks()
    {
        var bead = new BeadRecord
        {
            Id = "wi-bbbb11112222",
            Title = "dependent",
            Dependencies = [new BeadDependency { DependsOnId = "wi-aaaa11112222" }]
        };

        // `blocks` is bd's own default for an edge with no type given, and the unsafe direction here
        // is dropping a real blocker rather than keeping a spurious one.
        Assert.Equal(["wi-aaaa11112222"], BeadMapper.ToWorkItem(bead).DependsOn);
    }

    [Fact]
    public void AReversedDependencyEdgeIsNotReadAsTheItemDependingOnItself()
    {
        var bead = new BeadRecord
        {
            Id = "wi-aaaa11112222",
            Title = "blocker",
            Dependencies =
            [
                new BeadDependency
                {
                    IssueId = "wi-bbbb11112222",
                    DependsOnId = "wi-aaaa11112222",
                    Type = "blocks"
                }
            ]
        };

        Assert.Empty(BeadMapper.ToWorkItem(bead).DependsOn);
    }
}
