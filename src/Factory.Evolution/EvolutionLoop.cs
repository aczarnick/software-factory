using System.Text;
using Factory.Agents;
using Factory.Core;

namespace Factory.Evolution;

public sealed record EvolveWorkItemDto
{
    public string Title { get; init; } = "";
    public string Intent { get; init; } = "";
}

public sealed record EvolveProposal
{
    public bool ProposeChange { get; init; }
    public string? Prompt { get; init; }
    public string Rationale { get; init; } = "";
    public List<EvolveWorkItemDto> WorkItems { get; init; } = [];
}

public sealed record EvolutionOutcome
{
    public required string StationId { get; init; }
    public PromotionDecision? Decision { get; init; }
    public PromptVersion? PromotedTo { get; init; }
    public PromptVersion? NewChallenger { get; init; }
    public IReadOnlyList<WorkItem> ImprovementItems { get; init; } = [];
    public decimal CostUsd { get; init; }
    public string Summary { get; init; } = "";
}

/// <summary>
/// The self-improvement mechanism.
///
/// Each pass does three things for a station: settles any challenger currently under trial
/// through the promotion gate, proposes a new challenger when the champion has accumulated
/// enough evidence to learn from, and turns observed defects into real work items filed back
/// into the factory's own backlog. The last part is what closes the loop — the factory's
/// failures become the factory's work.
/// </summary>
public sealed class EvolutionLoop(PromptRegistry prompts, AgentRunner runner, Action<string>? log = null)
{
    public const string EvolveSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"proposeChange\":{\"type\":\"boolean\"}," +
        "\"prompt\":{\"type\":\"string\"}," +
        "\"rationale\":{\"type\":\"string\"}," +
        "\"workItems\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{" +
        "\"title\":{\"type\":\"string\"},\"intent\":{\"type\":\"string\"}}," +
        "\"required\":[\"title\"]}}}," +
        "\"required\":[\"proposeChange\",\"rationale\"]}";

    /// <summary>A challenger may not balloon: every token is paid on every run forever, so a
    /// prompt that wins on quality while tripling in size is not an improvement.</summary>
    private const double MaxGrowthFactor = 2.0;

    private void Log(string message) => log?.Invoke(message);

    public async Task<EvolutionOutcome> RunStationAsync(
        string stationId,
        ModelTier tier,
        IReadOnlyList<RunRecord> runs,
        IReadOnlyList<string> failureTraces,
        decimal budgetUsd,
        GateSettings? settings = null,
        CancellationToken ct = default)
    {
        var cfg = settings ?? GateSettings.Default;
        var pointer = prompts.Pointer(stationId);
        var champion = prompts.Champion(stationId);
        var stats = Evaluator.ByVersion(runs, stationId);

        var championStats = stats.FirstOrDefault(s => s.Version == champion.Id)
                            ?? Empty(stationId, champion.Id);

        PromptStats? challengerStats = null;
        PromptVersion? challenger = null;
        if (pointer.Challenger is { } cv && prompts.Get(stationId, cv) is { } c)
        {
            challenger = c;
            challengerStats = stats.FirstOrDefault(s => s.Version == c.Id) ?? Empty(stationId, c.Id);
        }

        // 1. Settle any trial in progress.
        var decision = PromotionGate.Decide(championStats, challengerStats, cfg);
        PromptVersion? promoted = null;

        switch (decision.Action)
        {
            case PromotionAction.Promote when challenger is not null:
                prompts.SetChampion(stationId, challenger.Version);
                promoted = challenger;
                champion = challenger;
                championStats = challengerStats!;
                challenger = null;
                Log($"promoted {promoted.Id}: {decision.Rationale}");
                break;

            case PromotionAction.Demote when challenger is not null:
                prompts.ClearChallenger(stationId);
                Log($"discarded {challenger.Id}: {decision.Rationale}");
                challenger = null;
                break;
        }

        // 2. Propose a new challenger only when there is evidence to learn from.
        if (challenger is not null)
        {
            return new EvolutionOutcome
            {
                StationId = stationId,
                Decision = decision,
                PromotedTo = promoted,
                Summary = $"{stationId}: trial continues — {decision.Rationale}"
            };
        }

        if (championStats.Runs < cfg.MinChampionSamples)
        {
            return new EvolutionOutcome
            {
                StationId = stationId,
                Decision = decision,
                PromotedTo = promoted,
                Summary = $"{stationId}: {championStats.Runs}/{cfg.MinChampionSamples} runs before proposing a challenger"
            };
        }

        var (proposal, cost) = await ProposeAsync(
            stationId, tier, champion, championStats, failureTraces, budgetUsd, ct).ConfigureAwait(false);

        if (proposal is null)
        {
            return new EvolutionOutcome
            {
                StationId = stationId,
                Decision = decision,
                PromotedTo = promoted,
                CostUsd = cost,
                Summary = $"{stationId}: no proposal produced"
            };
        }

        var improvements = proposal.WorkItems
            .Where(w => !string.IsNullOrWhiteSpace(w.Title))
            .Select(w => WorkItem.Create(w.Title, w.Intent, WorkItemKind.Improvement) with
            {
                Provenance = Provenance.FromEvolution(stationId),
                State = WorkItemState.Draft,
                Priority = Priorities.Lowest
            })
            .ToList();

        PromptVersion? newChallenger = null;
        string summary;

        if (proposal.ProposeChange && proposal.Prompt is { Length: > 40 } text && text != champion.Text)
        {
            if (text.Length > champion.Text.Length * MaxGrowthFactor)
            {
                summary = $"{stationId}: challenger rejected before trial — {text.Length} chars against " +
                          $"champion's {champion.Text.Length}, beyond the {MaxGrowthFactor:F1}x growth limit";
            }
            else
            {
                newChallenger = prompts.Add(stationId, text);
                prompts.SetChallenger(stationId, newChallenger.Version, pointer.ChallengerShare);
                summary = $"{stationId}: challenger {newChallenger.Id} under trial — {proposal.Rationale}";
                Log(summary);
            }
        }
        else
        {
            summary = $"{stationId}: left alone — {proposal.Rationale}";
        }

        if (improvements.Count > 0)
            summary += $" · filed {improvements.Count} improvement item(s)";

        return new EvolutionOutcome
        {
            StationId = stationId,
            Decision = decision,
            PromotedTo = promoted,
            NewChallenger = newChallenger,
            ImprovementItems = improvements,
            CostUsd = cost,
            Summary = summary
        };
    }

    private async Task<(EvolveProposal? Proposal, decimal Cost)> ProposeAsync(
        string stationId, ModelTier tier, PromptVersion champion, PromptStats stats,
        IReadOnlyList<string> failureTraces, decimal budgetUsd, CancellationToken ct)
    {
        var evolvePrompt = prompts.Champion("evolve");

        var sb = new StringBuilder();
        sb.AppendLine($"Station under review: {stationId}");
        sb.AppendLine($"Measured: {stats.Describe}");
        sb.AppendLine($"Error rate: {stats.ErrorRate:P0} · retry rate: {stats.RetryRate:P0}");
        sb.AppendLine();
        sb.AppendLine("Current champion prompt:");
        sb.AppendLine("---");
        sb.AppendLine(champion.Text);
        sb.AppendLine("---");

        if (failureTraces.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Worst runs:");
            foreach (var trace in failureTraces.Take(12))
                sb.AppendLine($"- {Cap(trace, 400)}");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("No failures recorded. Only propose a change if it reduces cost without " +
                          "reducing quality; otherwise return proposeChange: false.");
        }

        var request = new AgentRequest
        {
            Prompt = sb.ToString(),
            Profile = AgentProfile.Thin(tier, evolvePrompt.Text, "evolve"),
            JsonSchema = EvolveSchema,
            MaxBudgetUsd = budgetUsd,
            NoCache = true
        };

        var run = await runner.RunAsync(request, ct: ct).ConfigureAwait(false);
        if (!run.Success) return (null, run.CostUsd);

        return run.TryStructured<EvolveProposal>(out var proposal, out _)
            ? (proposal, run.CostUsd)
            : (null, run.CostUsd);
    }

    private static PromptStats Empty(string stationId, string version) =>
        new() { StationId = stationId, Version = version };

    private static string Cap(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
