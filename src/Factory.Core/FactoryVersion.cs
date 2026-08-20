using System.Reflection;

namespace Factory.Core;

/// <summary>
/// Identity of the harness itself.
///
/// Prompts are versioned and work items are ledgered, but without this the run history is
/// not attributable: a prompt whose pass rate moved between last week and today may have
/// been affected by a change to the harness rather than to the prompt. Recording the factory
/// version on every run keeps that comparison honest.
/// </summary>
public static class FactoryVersion
{
    /// <summary>Semantic version of the running build.</summary>
    public static string Version { get; } =
        typeof(FactoryVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? typeof(FactoryVersion).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>Commit the factory's own source was built from, when it can be determined.</summary>
    public static string? Commit { get; } =
        typeof(FactoryVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+') is { Length: > 1 } parts
            ? parts[1][..Math.Min(12, parts[1].Length)]
            : null;

    public static string Full => Commit is null ? Version : $"{Version}+{Commit}";

    /// <summary>Major version of the plugin ABI. Bump only on a breaking change to
    /// <see cref="IWorkItemStore"/>, <see cref="IRunHistory"/>, <see cref="IRunHistorySink"/>,
    /// or any type they expose. A plugin built against a different major is refused at load.
    ///
    /// v2 is the storage-ports work: <see cref="WorkItem"/> gained <see cref="WorkItem.Owner"/> and
    /// <see cref="WorkItem.StoreStatus"/>, and <see cref="WorkItemKind"/> gained five members —
    /// all in a type <see cref="IWorkItemStore"/> and <see cref="IRunHistorySink"/> expose. None of
    /// them can raise a <c>MissingMethodException</c>, so the exposure is narrow: a contract-1 sink
    /// handed <c>Kind = Task</c> with no case for it. Bumped anyway, and now rather than later,
    /// because the alternative was a promise recorded outside the repository that a plan slipping
    /// would silently skip while the gate that exists for this never fired.
    ///
    /// v3 adds <see cref="WorkItemState.Superseded"/>, so a decomposed parent stops being reported as
    /// Done. Same shape of exposure as v2's: no signature moved, but a contract-2 store or sink can be
    /// handed a state it has no case for, and one that silently maps to Done would reinstate exactly
    /// the false green this member exists to remove.
    ///
    /// v4 adds the <see cref="CriteriaVerified"/> event, so per-criterion outcomes survive as ledger
    /// evidence instead of being computed and discarded. <see cref="IRunHistorySink"/> takes
    /// <see cref="FactoryEvent"/>, so a contract-3 sink is now handed a case it does not know — the
    /// same exposure v2 and v3 were bumped for.
    ///
    /// v5 adds <see cref="RunRecord.SessionId"/>. <see cref="IRunHistory"/> and
    /// <see cref="IRunHistorySink"/> both expose <see cref="RunRecord"/>, so this is the same class
    /// of widening as v2's. A contract-4 history that drops the field on write turns every run it
    /// stores back into an untraceable scalar, which is the state this member exists to end.</summary>
    public const int ContractVersion = 5;
}
