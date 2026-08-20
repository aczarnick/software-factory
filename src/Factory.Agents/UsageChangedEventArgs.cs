namespace Factory.Agents;

/// <summary>Describes a change in what the usage governor will allow.</summary>
public sealed class UsageChangedEventArgs(string message) : EventArgs
{
    /// <summary>Human-readable description of the change, for callers that report it.</summary>
    public string Message { get; } = message;
}
