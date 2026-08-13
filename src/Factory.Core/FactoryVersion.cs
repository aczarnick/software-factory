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
}
