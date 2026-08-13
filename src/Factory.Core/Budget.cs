namespace Factory.Core;

public sealed record BudgetSpec
{
    public decimal DailyUsd { get; init; } = 25m;
    public decimal PerItemUsd { get; init; } = 3m;
    public decimal PerRunUsd { get; init; } = 1m;

    /// <summary>Fraction of the daily budget that self-improvement work may consume.
    /// Caps the evolution loop so it can never starve user work.</summary>
    public decimal EvolutionShare { get; init; } = 0.15m;
}

public sealed class BudgetExhaustedException(string scope, decimal limit, decimal spent)
    : Exception($"Budget exhausted for {scope}: spent ${spent:F4} of ${limit:F4} ceiling.")
{
    public string Scope { get; } = scope;
    public decimal Limit { get; } = limit;
    public decimal Spent { get; } = spent;
}

/// <summary>
/// Enforces spend ceilings at run, item, and daily scope. Checks happen before dispatch,
/// so the factory refuses to start work it cannot afford rather than discovering the
/// overspend afterwards.
/// </summary>
public sealed class BudgetGuard(BudgetSpec spec, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, decimal> _perItem = [];
    private decimal _daily;
    private decimal _evolutionDaily;
    private DateOnly _day;

    public BudgetSpec Spec { get; } = spec;

    public decimal DailySpent { get { lock (_gate) { RollDay(); return _daily; } } }
    public decimal EvolutionSpent { get { lock (_gate) { RollDay(); return _evolutionDaily; } } }

    public decimal SpentOn(string itemId)
    {
        lock (_gate) return _perItem.GetValueOrDefault(itemId);
    }

    private void RollDay()
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        if (today == _day) return;
        _day = today;
        _daily = 0m;
        _evolutionDaily = 0m;
    }

    /// <summary>Throws if the next run cannot be afforded. Call before dispatch.</summary>
    public void EnsureCanSpend(WorkItem item)
    {
        lock (_gate)
        {
            RollDay();

            if (_daily >= Spec.DailyUsd)
                throw new BudgetExhaustedException("daily", Spec.DailyUsd, _daily);

            var itemLimit = item.BudgetUsd ?? Spec.PerItemUsd;
            var itemSpent = _perItem.GetValueOrDefault(item.Id);
            if (itemSpent >= itemLimit)
                throw new BudgetExhaustedException($"item {item.Id}", itemLimit, itemSpent);

            if (item.Provenance.Kind == ProvenanceKind.Evolution)
            {
                var evoLimit = Spec.DailyUsd * Spec.EvolutionShare;
                if (_evolutionDaily >= evoLimit)
                    throw new BudgetExhaustedException("evolution/daily", evoLimit, _evolutionDaily);
            }
        }
    }

    public bool CanSpend(WorkItem item)
    {
        try { EnsureCanSpend(item); return true; }
        catch (BudgetExhaustedException) { return false; }
    }

    /// <summary>Ceiling to hand the transport for the next run: the smaller of the
    /// station/run cap and what remains of the item's allowance.</summary>
    public decimal RemainingForRun(WorkItem item, decimal? stationCap)
    {
        lock (_gate)
        {
            RollDay();
            var itemLimit = item.BudgetUsd ?? Spec.PerItemUsd;
            var itemRemaining = Math.Max(0m, itemLimit - _perItem.GetValueOrDefault(item.Id));
            var dailyRemaining = Math.Max(0m, Spec.DailyUsd - _daily);
            var cap = stationCap ?? Spec.PerRunUsd;
            return Math.Min(Math.Min(cap, itemRemaining), dailyRemaining);
        }
    }

    public decimal Record(WorkItem item, decimal usd)
    {
        lock (_gate)
        {
            RollDay();
            _daily += usd;
            if (item.Provenance.Kind == ProvenanceKind.Evolution) _evolutionDaily += usd;
            var total = _perItem.GetValueOrDefault(item.Id) + usd;
            _perItem[item.Id] = total;
            return total;
        }
    }

    /// <summary>Rehydrate accumulators from recorded history so restarts do not reset spend.</summary>
    public void Restore(BudgetRestoreView view)
    {
        lock (_gate)
        {
            _day = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
            _daily = view.DailyUsd;
            _evolutionDaily = view.EvolutionDailyUsd;
            _perItem.Clear();
            foreach (var (itemId, usd) in view.PerItemUsd) _perItem[itemId] = usd;
        }
    }
}
