namespace Factory.Core;

/// <summary>The three accumulators <see cref="BudgetGuard.Restore(BudgetRestoreView)"/> needs.
/// Expressed as aggregates rather than raw runs so a database provider can compute them with
/// grouped queries.
///
/// <para><see cref="DailyUsd"/> and <see cref="EvolutionDailyUsd"/> arrive already bucketed to
/// "today" by the provider's own clock, while <see cref="BudgetGuard"/> stamps its day from its
/// clock. An implementor giving the two different time sources produces a guard that believes it
/// holds today's spend while holding another day's.</para></summary>
public sealed record BudgetRestoreView(
    IReadOnlyDictionary<string, decimal> PerItemUsd,
    decimal DailyUsd,
    decimal EvolutionDailyUsd)
{
    public static readonly BudgetRestoreView Empty =
        new(new Dictionary<string, decimal>(), 0m, 0m);
}
