using Factory.Core;

namespace Factory.Runtime;

/// <summary>
/// Station output contracts. Stations return JSON validated against these schemas by the
/// transport, so the harness never parses prose. Keeping the shapes small also bounds
/// output tokens, which is why several fields that would be pleasant to have are absent.
/// </summary>
public static class Schemas
{
    private const string VerificationSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"kind\":{\"type\":\"string\",\"enum\":[\"command\",\"tests\",\"file\",\"judge\"]}," +
        "\"command\":{\"type\":\"string\"}," +
        "\"path\":{\"type\":\"string\"}," +
        "\"rubric\":{\"type\":\"string\"}}," +
        "\"required\":[\"kind\"]}";

    private static string ItemSchema(bool withKey) =>
        "{\"type\":\"object\",\"properties\":{" +
        (withKey ? "\"key\":{\"type\":\"string\"}," : "") +
        (withKey ? "\"dependsOn\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," : "") +
        "\"title\":{\"type\":\"string\"}," +
        "\"intent\":{\"type\":\"string\"}," +
        "\"kind\":{\"type\":\"string\",\"enum\":[\"Feature\",\"Bug\",\"Chore\",\"Refactor\",\"Spike\",\"Improvement\"]}," +
        "\"requirements\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," +
        "\"acceptanceCriteria\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{" +
        "\"statement\":{\"type\":\"string\"}," +
        "\"verification\":" + VerificationSchema + "}," +
        "\"required\":[\"statement\",\"verification\"]}}," +
        "\"assumptions\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}}," +
        "\"required\":[\"title\",\"kind\",\"requirements\",\"acceptanceCriteria\"]}";

    public static string Intake =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"items\":{\"type\":\"array\",\"items\":" + ItemSchema(false) + "}," +
        "\"questions\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}}," +
        "\"required\":[\"items\"]}";

    public static string Decompose =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"children\":{\"type\":\"array\",\"items\":" + ItemSchema(true) + "}}," +
        "\"required\":[\"children\"]}";

    public const string Plan =
        """
        {"type":"object","properties":{
          "files":{"type":"array","items":{"type":"object","properties":{
              "path":{"type":"string"},"change":{"type":"string"}},
            "required":["path","change"]}},
          "steps":{"type":"array","items":{"type":"string"}},
          "risks":{"type":"array","items":{"type":"string"}},
          "verifyCommand":{"type":"string"}},
         "required":["files","steps"]}
        """;

    public const string Review =
        """
        {"type":"object","properties":{
          "pass":{"type":"boolean"},
          "summary":{"type":"string"},
          "findings":{"type":"array","items":{"type":"string"}},
          "followUp":{"type":"array","items":{"type":"string"}}},
         "required":["pass","summary"]}
        """;

    // The evolve station's contract lives with the evolution loop that owns it:
    // Factory.Evolution.EvolutionLoop.EvolveSchema.
}

public sealed record VerificationDto
{
    public string Kind { get; init; } = "command";
    public string? Command { get; init; }
    public string? Path { get; init; }
    public string? Rubric { get; init; }

    public Verification ToDomain() => Kind.ToLowerInvariant() switch
    {
        "command" when !string.IsNullOrWhiteSpace(Command) => new CommandVerification(Command),
        "tests" when !string.IsNullOrWhiteSpace(Command) => new TestsPassVerification(Command),
        "file" when !string.IsNullOrWhiteSpace(Path) => new FileExistsVerification(Path),
        "judge" => new AgentJudgeVerification(Rubric ?? "Criterion is satisfied."),
        // A malformed verification degrades to a judged one rather than silently passing.
        _ => new AgentJudgeVerification(Rubric ?? Command ?? Path ?? "Criterion is satisfied.")
    };
}

public sealed record CriterionDto
{
    public string Statement { get; init; } = "";
    public VerificationDto Verification { get; init; } = new();

    public AcceptanceCriterion ToDomain() => new()
    {
        Id = Ids.New("ac"),
        Statement = Statement,
        Verification = Verification.ToDomain()
    };
}

public sealed record WorkItemDto
{
    /// <summary>Local key used by siblings in <see cref="DependsOn"/>.</summary>
    public string? Key { get; init; }

    public string Title { get; init; } = "";
    public string Intent { get; init; } = "";
    public string Kind { get; init; } = "Feature";
    public List<string> Requirements { get; init; } = [];
    public List<CriterionDto> AcceptanceCriteria { get; init; } = [];
    public List<string> Assumptions { get; init; } = [];
    public List<string> DependsOn { get; init; } = [];

    public WorkItem ToDomain(string? parentId = null, Provenance? provenance = null) => new()
    {
        Id = Ids.New("wi"),
        Title = Title,
        Intent = Intent,
        Kind = Enum.TryParse<WorkItemKind>(Kind, ignoreCase: true, out var k) ? k : WorkItemKind.Feature,
        Requirements = Requirements,
        AcceptanceCriteria = AcceptanceCriteria.Select(c => c.ToDomain()).ToList(),
        Assumptions = Assumptions,
        ParentId = parentId,
        Provenance = provenance ?? Provenance.Human,
        State = WorkItemState.Draft
    };
}

public sealed record IntakeResult
{
    public List<WorkItemDto> Items { get; init; } = [];
    public List<string> Questions { get; init; } = [];
}

public sealed record DecomposeResult
{
    public List<WorkItemDto> Children { get; init; } = [];
}

public sealed record PlanFile
{
    public string Path { get; init; } = "";
    public string Change { get; init; } = "";
}

public sealed record PlanResult
{
    public List<PlanFile> Files { get; init; } = [];
    public List<string> Steps { get; init; } = [];
    public List<string> Risks { get; init; } = [];
    public string? VerifyCommand { get; init; }

    public string ToPromptText()
    {
        var lines = new List<string>();
        if (Files.Count > 0)
        {
            lines.Add("Files to change:");
            lines.AddRange(Files.Select(f => $"  - {f.Path}: {f.Change}"));
        }
        if (Steps.Count > 0)
        {
            lines.Add("Steps:");
            lines.AddRange(Steps.Select((s, i) => $"  {i + 1}. {s}"));
        }
        if (Risks.Count > 0)
        {
            lines.Add("Risks:");
            lines.AddRange(Risks.Select(r => $"  - {r}"));
        }
        return string.Join("\n", lines);
    }
}

public sealed record ReviewResult
{
    public bool Pass { get; init; }
    public string Summary { get; init; } = "";
    public List<string> Findings { get; init; } = [];
    public List<string> FollowUp { get; init; } = [];
}

