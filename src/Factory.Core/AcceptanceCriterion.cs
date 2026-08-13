using System.Text.Json.Serialization;

namespace Factory.Core;

/// <summary>
/// How a criterion is proven. Deterministic verifications cost zero tokens and are always
/// preferred: they are simultaneously the quality mechanism (the factory cannot claim a
/// success it did not demonstrate) and the largest single token-reduction lever, since
/// verification is otherwise the most repeated model call in the system.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CommandVerification), "command")]
[JsonDerivedType(typeof(TestsPassVerification), "tests")]
[JsonDerivedType(typeof(FileExistsVerification), "file")]
[JsonDerivedType(typeof(AgentJudgeVerification), "judge")]
public abstract record Verification
{
    /// <summary>True when this can be checked without invoking a model.</summary>
    [JsonIgnore]
    public abstract bool IsDeterministic { get; }

    [JsonIgnore]
    public abstract string Describe { get; }
}

/// <summary>Run a shell command; pass on the expected exit code (and optional stdout match).</summary>
public sealed record CommandVerification(
    string Command,
    int ExpectExitCode = 0,
    string? ExpectStdoutMatch = null,
    int TimeoutSeconds = 300) : Verification
{
    public override bool IsDeterministic => true;
    public override string Describe => $"`{Command}` exits {ExpectExitCode}";
}

/// <summary>Run a project test suite.</summary>
public sealed record TestsPassVerification(string Command, int TimeoutSeconds = 900) : Verification
{
    public override bool IsDeterministic => true;
    public override string Describe => $"tests pass via `{Command}`";
}

/// <summary>A path must exist relative to the workspace root.</summary>
public sealed record FileExistsVerification(string Path) : Verification
{
    public override bool IsDeterministic => true;
    public override string Describe => $"`{Path}` exists";
}

/// <summary>Last resort: a model judges against a rubric. Costs tokens, so intake is
/// instructed to avoid it where a command would do.</summary>
public sealed record AgentJudgeVerification(string Rubric) : Verification
{
    public override bool IsDeterministic => false;
    public override string Describe => $"judged: {Rubric}";
}

public sealed record AcceptanceCriterion
{
    public required string Id { get; init; }
    public required string Statement { get; init; }
    public required Verification Verification { get; init; }

    public static AcceptanceCriterion Command(string statement, string command) =>
        new() { Id = Ids.New("ac"), Statement = statement, Verification = new CommandVerification(command) };

    public static AcceptanceCriterion Tests(string statement, string command) =>
        new() { Id = Ids.New("ac"), Statement = statement, Verification = new TestsPassVerification(command) };

    public static AcceptanceCriterion FileExists(string statement, string path) =>
        new() { Id = Ids.New("ac"), Statement = statement, Verification = new FileExistsVerification(path) };

    public static AcceptanceCriterion Judged(string statement, string rubric) =>
        new() { Id = Ids.New("ac"), Statement = statement, Verification = new AgentJudgeVerification(rubric) };
}

public sealed record CriterionResult(string CriterionId, bool Passed, string Detail)
{
    public static CriterionResult Pass(string id, string detail = "") => new(id, true, detail);
    public static CriterionResult Fail(string id, string detail) => new(id, false, detail);
}

public sealed record VerificationReport(IReadOnlyList<CriterionResult> Results)
{
    public bool AllPassed => Results.Count > 0 && Results.All(r => r.Passed);
    public IEnumerable<CriterionResult> Failures => Results.Where(r => !r.Passed);

    public string Summary => AllPassed
        ? $"all {Results.Count} criteria passed"
        : $"{Failures.Count()}/{Results.Count} criteria failed: " +
          string.Join("; ", Failures.Select(f => $"{f.CriterionId}: {f.Detail}"));
}
