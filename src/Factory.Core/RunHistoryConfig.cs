namespace Factory.Core;

/// <summary>The durable local writer, plus any additional best-effort sinks. The writer is
/// always present: it is the copy the prompt promotion gate mines.</summary>
public sealed record RunHistoryConfig
{
    public string Writer { get; init; } = "jsonl";
    public IReadOnlyList<ProviderRef> Sinks { get; init; } = [];
}
