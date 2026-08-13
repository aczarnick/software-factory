namespace Factory.Core;

/// <summary>Durable local record of everything the factory did. Always present: this is the
/// copy the prompt promotion gate mines, so it must survive a sink being unreachable.</summary>
public interface IRunHistory : IDisposable
{
    void Append(FactoryEvent evt);

    /// <summary>Events with a sequence strictly greater than <paramref name="afterSeq"/>,
    /// in order. Pass 0 for the whole history.</summary>
    IEnumerable<FactoryEvent> ReadFrom(long afterSeq);

    IReadOnlyList<RunRecord> RunsForItem(string itemId);
    IReadOnlyList<RunRecord> RunsForStation(string stationId);
    SpendTotals Totals();
    BudgetRestoreView ForBudget();

    /// <summary>Current champion prompt version per station.</summary>
    IReadOnlyDictionary<string, string> Champions();
}
