using Factory.Core;
using Factory.Evolution;

namespace Factory.Runtime;

public sealed record EvolutionReport
{
    public IReadOnlyList<EvolutionOutcome> Outcomes { get; init; } = [];
    public decimal CostUsd { get; init; }
    public int Promoted { get; init; }
    public int Challengers { get; init; }
    public int ItemsFiled { get; init; }

    public string Summary =>
        $"{Promoted} promoted, {Challengers} new challenger(s), {ItemsFiled} improvement item(s) filed · ${CostUsd:F4}";
}

/// <summary>
/// Binds the evolution loop to a deployed factory: reads its run history, applies promotion
/// decisions to its prompt registry, records the lineage in its ledger, and files the
/// improvement work the loop identifies back into its own backlog.
/// </summary>
public sealed class EvolutionService(FactoryHost host)
{
    private readonly FactoryServices _s = host.Services;

    /// <summary>Stations worth evolving: those that actually call a model.</summary>
    public IEnumerable<StationDef> EvolvableStations() =>
        _s.Blueprint.Stations.Where(s =>
            s.Profile != TokenProfile.None &&
            s.Role != StationRole.Evolve &&
            s.Role != StationRole.Delegate);

    public async Task<EvolutionReport> RunAsync(
        GateSettings? settings = null, CancellationToken ct = default)
    {
        var loop = new EvolutionLoop(_s.Prompts, _s.Runner, msg => _s.Log($"  [evolve] {msg}"));
        var runs = _s.State.Runs;
        var traces = FailureTraces();

        var outcomes = new List<EvolutionOutcome>();
        decimal cost = 0;
        int promoted = 0, challengers = 0, filed = 0;

        var evolveDef = _s.Blueprint.Station("evolve");
        var tier = evolveDef?.Tier ?? ModelTier.Sonnet;
        var perStationBudget = evolveDef?.BudgetUsd ?? 0.50m;

        foreach (var station in EvolvableStations())
        {
            ct.ThrowIfCancellationRequested();

            var stationTraces = traces.GetValueOrDefault(station.Id) ?? [];

            var outcome = await loop.RunStationAsync(
                station.Id, tier, runs, stationTraces, perStationBudget, settings, ct).ConfigureAwait(false);

            outcomes.Add(outcome);
            cost += outcome.CostUsd;

            if (outcome.PromotedTo is { } promotedVersion && outcome.Decision is { } decision)
            {
                _s.Record(new PromptPromoted(
                    station.Id,
                    _s.State.Champions.GetValueOrDefault(station.Id) ?? "v?",
                    promotedVersion.Id,
                    decision.FitnessDelta,
                    decision.Rationale));
                promoted++;
            }
            else if (outcome.Decision is { Action: PromotionAction.Demote } demoted)
            {
                _s.Record(new PromptDemoted(
                    station.Id, "challenger", _s.Prompts.Champion(station.Id).Id, demoted.Rationale));
            }

            if (outcome.NewChallenger is not null) challengers++;

            foreach (var item in outcome.ImprovementItems)
            {
                host.Submit(item, activate: false);
                filed++;
            }

            _s.Log($"  {outcome.Summary}");
        }

        return new EvolutionReport
        {
            Outcomes = outcomes,
            CostUsd = cost,
            Promoted = promoted,
            Challengers = challengers,
            ItemsFiled = filed
        };
    }

    /// <summary>Failed gate verdicts per station — the evidence the optimiser reasons over.
    /// Only failures are collected: a successful run teaches the loop nothing it can act on,
    /// and sending them would inflate the prompt for no gain.</summary>
    private Dictionary<string, List<string>> FailureTraces()
    {
        var traces = new Dictionary<string, List<string>>();

        foreach (var evt in _s.Ledger.ReadAll())
        {
            if (evt is not GateEvaluated { Passed: false } gate) continue;
            if (!traces.TryGetValue(gate.StationId, out var list))
                traces[gate.StationId] = list = [];
            list.Add(gate.Detail);
        }

        // Most recent failures first: they reflect the current champion.
        foreach (var list in traces.Values) list.Reverse();
        return traces;
    }
}
