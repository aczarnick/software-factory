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
    public void Every_work_item_state_maps_to_a_status_and_back()
    {
        foreach (var state in Enum.GetValues<WorkItemState>())
            Assert.Equal(state, BeadMapper.StateFor(BeadMapper.StatusFor(state)));
    }

    [Fact]
    public void Every_work_item_kind_maps_to_a_type_and_back()
    {
        foreach (var kind in Enum.GetValues<WorkItemKind>())
            Assert.Equal(kind, BeadMapper.KindFor(BeadMapper.TypeFor(kind)));
    }

    [Fact]
    public void Create_args_carry_the_explicit_id_and_native_fields()
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
    public void Create_args_declare_every_dependency_as_a_blocker()
    {
        var item = WorkItem.Create("second") with { DependsOn = ["wi-aaaa11112222", "wi-bbbb11112222"] };

        var args = BeadMapper.CreateArgs(item);

        Assert.Contains("depends-on:wi-aaaa11112222", args);
        Assert.Contains("depends-on:wi-bbbb11112222", args);
    }

    [Fact]
    public void Structured_criteria_survive_the_metadata_round_trip()
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
    public void The_structured_remainder_survives_the_metadata_round_trip()
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
    public void Native_fields_are_read_from_the_bead_rather_than_its_metadata()
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
    public void Blockers_are_restored_as_the_dependencies_of_the_item_they_block()
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
    public void A_bead_with_no_metadata_still_maps()
    {
        var restored = BeadMapper.ToWorkItem(new BeadRecord { Id = "wi-cccc11112222", Title = "bare" });

        Assert.Equal("bare", restored.Title);
        Assert.Empty(restored.AcceptanceCriteria);
        Assert.Empty(restored.DependsOn);
    }

    [Fact]
    public void Volatile_run_state_is_not_sent_to_the_backlog()
    {
        var item = WorkItem.Create("thing") with
        {
            Station = "implement",
            Worktree = "/tmp/wt",
            Attempts = 3,
            SpentUsd = 0.42m
        };

        var metadata = BeadMapper.MetadataFor(item);

        Assert.DoesNotContain("worktree", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("spentUsd", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("attempts", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("implement", metadata, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refactor_and_improvement_map_to_custom_types()
    {
        Assert.Equal("refactor", BeadMapper.TypeFor(WorkItemKind.Refactor));
        Assert.Equal("improvement", BeadMapper.TypeFor(WorkItemKind.Improvement));
    }

    [Fact]
    public void Update_args_carry_the_mapped_status()
    {
        var item = WorkItem.Create("thing") with { State = WorkItemState.Verified };

        var args = BeadMapper.UpdateArgs(item);

        Assert.Contains("--status", args);
        Assert.Contains("verified", args);
    }

    [Fact]
    public void An_unmapped_status_is_refused_rather_than_guessed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BeadMapper.StateFor("deferred"));
    }

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
    public void Output_beads_really_emits_deserialises_into_every_mapped_field()
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
    public void Blockers_beads_really_emits_become_the_items_dependencies(string fixture)
    {
        // `show` embeds the blocking issue and `list` reports the edge, so reading only one of the
        // two shapes loses every dependency edge from whichever command was not sampled.
        var json = fixture == nameof(RealBlockedBeadFromShow) ? RealBlockedBeadFromShow : RealBlockedBeadFromList;

        var item = BeadMapper.ToWorkItem(FactoryJson.Read<BeadRecord>(json)!);

        Assert.Equal(["wi-aaaa11112222"], item.DependsOn);
    }

    [Fact]
    public void A_reversed_dependency_edge_is_not_read_as_the_item_depending_on_itself()
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
