using Factory.Core;

namespace Factory.Runtime;

/// <summary>Thin synchronous wrapper over the <c>bd</c> executable.</summary>
public sealed class BeadsCli(string workingDirectory)
{
    private static readonly Dictionary<string, string> NonInteractive =
        new() { ["BD_NON_INTERACTIVE"] = "1" };

    public ShellResult Exec(params string[] args) =>
        Shell.Run("bd", args, workingDirectory, NonInteractive);

    /// <summary>Runs a command expected to emit JSON, failing loudly when it does not. <c>bd</c>
    /// returns an array for <c>show</c> and <c>list</c> and an object for <c>create</c>, so both
    /// shapes deserialise into a collection.</summary>
    public IReadOnlyList<T> Json<T>(params string[] args)
    {
        var result = Exec(args);
        if (!result.Ok)
            throw new InvalidOperationException($"bd {string.Join(' ', args)} failed: {result.Combined}");

        return Parse<T>(result.Stdout, args);
    }

    /// <summary>Deserialises a single JSON object, for commands that summarise rather than list.</summary>
    public T? JsonObject<T>(params string[] args)
    {
        var result = Exec(args);
        if (!result.Ok)
            throw new InvalidOperationException($"bd {string.Join(' ', args)} failed: {result.Combined}");

        var text = Captured(result.Stdout, args);
        return string.IsNullOrEmpty(text) || text == "null" ? default : FactoryJson.Read<T>(text);
    }

    private static IReadOnlyList<T> Parse<T>(string stdout, string[] args)
    {
        var text = Captured(stdout, args);
        if (string.IsNullOrEmpty(text) || text == "null") return [];

        return text.StartsWith('[')
            ? FactoryJson.Read<List<T>>(text) ?? []
            : [FactoryJson.Read<T>(text)!];
    }

    // A capture at the retention bound is cut mid-document, so parsing it would fail as though bd
    // had emitted malformed JSON. Name the real cause instead: the backlog outgrew what one
    // command's output can carry.
    private static string Captured(string stdout, string[] args)
    {
        if (stdout.Length >= Shell.MaxCapturedOutputChars)
            throw new InvalidOperationException(
                $"bd {string.Join(' ', args)} produced more than {Shell.MaxCapturedOutputChars} " +
                "characters, so its JSON was truncated before it could be read.");

        return stdout.Trim();
    }
}
