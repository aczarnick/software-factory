namespace Factory.Core;

/// <summary>The three accumulators <see cref="BudgetGuard.Restore(BudgetRestoreView)"/> needs.
/// Expressed as aggregates rather than raw runs so a database provider can compute them with
/// grouped queries.</summary>
public sealed record BudgetRestoreView(
    IReadOnlyDictionary<string, decimal> PerItemUsd,
    decimal DailyUsd,
    decimal EvolutionDailyUsd)
{
    public static readonly BudgetRestoreView Empty =
        new(new Dictionary<string, decimal>(), 0m, 0m);
}
