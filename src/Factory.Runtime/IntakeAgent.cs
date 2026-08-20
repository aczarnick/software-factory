using System.Text;
using Factory.Agents;
using Factory.Core;

namespace Factory.Runtime;

public sealed record IntakeOutcome(
    IReadOnlyList<WorkItem> Items,
    IReadOnlyList<string> Questions,
    decimal CostUsd,
    string? Error)
{
    public bool NeedsAnswers => Questions.Count > 0 && Items.Count == 0;
    public bool Ok => Error is null;
}

/// <summary>
/// How work enters the factory. An agent — not a form — turns a request into work items with
/// requirements and acceptance criteria, and it is instructed to prefer criteria a machine
/// can check, because those are both the quality gate and the cheapest thing to verify.
///
/// Interactive mode asks clarifying questions; non-interactive mode proceeds on explicit,
/// recorded assumptions so the factory can be driven from a single prompt.
/// </summary>
public sealed class IntakeAgent(FactoryServices services)
{
    public async Task<IntakeOutcome> ElicitAsync(
        string request,
        IReadOnlyList<(string Question, string Answer)>? answers = null,
        bool allowQuestions = true,
        Provenance? provenance = null,
        CancellationToken ct = default)
    {
        var def = services.Blueprint.Station("intake")
                  ?? throw new InvalidOperationException("Blueprint has no intake station.");

        var prompt = services.Prompts.Select("intake", services.Rng);
        var digest = RepoDigest.Build(services.Workspace.RepoRoot, Math.Min(def.ContextByteCap, 4000));

        var sb = new StringBuilder();
        sb.AppendLine("Request:");
        sb.AppendLine(request);

        if (answers is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Clarifications already given:");
            foreach (var (q, a) in answers) sb.AppendLine($"Q: {q}\nA: {a}");
        }

        sb.AppendLine();
        sb.AppendLine("Repository digest:");
        sb.AppendLine(digest);

        if (!allowQuestions)
        {
            sb.AppendLine();
            sb.AppendLine(
                "Non-interactive: ask nothing. Decide every open question yourself, record each " +
                "decision in `assumptions`, and return items. `questions` must be empty.");
        }

        // Budget is tracked against a transient carrier so intake spend counts toward the
        // daily ceiling without polluting the backlog with a placeholder item.
        var carrier = WorkItem.Create("intake", request) with
        {
            Provenance = provenance ?? Provenance.Human
        };

        var agentRequest = new AgentRequest
        {
            Prompt = sb.ToString(),
            Profile = AgentProfile.Thin(def.Tier, prompt.Text, def.Id, def.MaxTurns),
            WorkingDirectory = services.Workspace.RepoRoot,
            JsonSchema = Schemas.Intake,
            MaxBudgetUsd = services.Budget.RemainingForRun(carrier, def.BudgetUsd),
            ContextDigest = Ids.Hash(prompt.Hash, request, string.Join("|", answers?.Select(a => a.Answer) ?? []))
        };

        var runId = Ids.New("run");
        services.Record(new RunStarted(runId, carrier.Id, def.Id, prompt.Id, ModelCatalog.Resolve(def.Tier)));

        var result = await services.Runner.RunAsync(agentRequest, ct: ct).ConfigureAwait(false);

        if (result.CostUsd > 0)
        {
            var total = services.Budget.Record(carrier, result.CostUsd);
            services.Record(new BudgetConsumed("intake", result.CostUsd, total));
        }

        services.Record(new RunCompleted(new RunRecord
        {
            RunId = runId,
            ItemId = carrier.Id,
            StationId = def.Id,
            PromptVersion = prompt.Id,
            Model = ModelCatalog.Resolve(def.Tier),
            Profile = def.Profile,
            Success = result.Success,
            GatePassed = result.Success,
            CostUsd = result.CostUsd,
            Usage = result.Usage,
            Turns = result.Turns,
            DurationMs = result.DurationMs,
            StopReason = result.StopReason,
            SessionId = result.SessionId,
            Error = result.Error,
            CacheHit = result.CacheHit
        }));

        if (!result.Success)
            return new IntakeOutcome([], [], result.CostUsd, result.Error ?? "intake failed");

        if (!result.TryStructured<IntakeResult>(out var parsed, out var parseError))
            return new IntakeOutcome([], [], result.CostUsd, $"unparseable intake output: {parseError}");

        if (allowQuestions && parsed.Questions.Count > 0 && parsed.Items.Count == 0)
            return new IntakeOutcome([], parsed.Questions, result.CostUsd, null);

        var items = parsed.Items
            .Select(dto => dto.ToDomain(provenance: provenance ?? Provenance.Human) with
            {
                State = WorkItemState.Draft
            })
            .ToList();

        return new IntakeOutcome(items, [], result.CostUsd, null);
    }
}
