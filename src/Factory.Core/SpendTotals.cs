namespace Factory.Core;

/// <summary>Aggregate spend across recorded runs, so a provider can answer `factory report`
/// with one query instead of returning every run for the caller to fold.</summary>
public sealed record SpendTotals(int RunCount, decimal TotalUsd, TokenUsage Usage)
{
    public static readonly SpendTotals Empty = new(0, 0m, TokenUsage.Zero);
}
