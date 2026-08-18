namespace Factory.Core;

/// <summary>A backlog provider failed. Never swallowed: the backlog has a single authority,
/// so continuing on a store that cannot answer would risk working the wrong queue.</summary>
public sealed class WorkItemStoreException(string provider, string operation, Exception inner)
    : Exception($"Work item store '{provider}' failed during {operation}: {inner.Message}", inner)
{
    public string Provider { get; } = provider;
    public string Operation { get; } = operation;
}
