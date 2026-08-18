using Factory.Core;

namespace Factory.Runtime;

/// <summary>Thin synchronous wrapper over the <c>bd</c> executable.</summary>
///
/// <remarks><c>owner</c> is set as <c>BEADS_NODE_ID</c> on every invocation, arming bd's
/// cross-replica guard for this checkout. Deliberately not <c>bd config set node_id</c>: that
/// writes the machine-global <c>~/.config/bd/config.yaml</c>, shared by every beads project on
/// the machine, and a value instead committed to the git-tracked <c>.beads/config.yaml</c> would
/// leave the guard armed but inert, since every clone would read the same name. The environment
/// variable is per-process and per-store instead. The id is one per <em>store</em>, not per host —
/// machines that are clients of the same shared Dolt database are one replica and must be given
/// the same <c>owner</c>, or the guard will treat them as distinct nodes racing each other.</remarks>
public sealed class BeadsCli(string workingDirectory, string owner)
{
    private readonly Dictionary<string, string> _environment =
        new() { ["BD_NON_INTERACTIVE"] = "1", ["BEADS_NODE_ID"] = owner };

    public ShellResult Exec(params string[] args) =>
        Shell.Run("bd", args, workingDirectory, _environment);

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
