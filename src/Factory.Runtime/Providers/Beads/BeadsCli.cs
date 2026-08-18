using Factory.Core;

namespace Factory.Runtime;

/// <summary>Thin synchronous wrapper over the <c>bd</c> executable.</summary>
///
/// <remarks><c>owner</c> is set as <c>BEADS_NODE_ID</c> on every invocation, arming bd's
/// cross-replica guard for this checkout. It has to be set at claim time, not only at reclaim
/// time: bd stamps the granting node onto the lease when the claim is taken, and a reclaim later
/// skips only a lease whose granting node differs from its own. Deliberately not <c>bd config set
/// node_id</c>: that writes the machine-global <c>~/.config/bd/config.yaml</c>, shared by every
/// beads project on the machine, and a value instead committed to the git-tracked
/// <c>.beads/config.yaml</c> would leave the guard armed but inert, since every clone would read
/// the same name. The environment variable is per-process and per-store instead.
///
/// The id is one per <em>store</em>, not per host — but "one store" means one <c>dolt
/// sql-server</c>: machines that are clients of the same server share one value, because they
/// share one lease table. This deployment mode has no such server (<c>bd dolt status</c> reports
/// <c>embedded (in-process, no server)</c>), so two machines syncing their own embedded copies of
/// a shared remote are <em>two</em> replicas and must be given <em>different</em> values of
/// <c>owner</c> — the same value here would make the guard treat a foreign lease as its own and
/// skip nothing, the same armed-but-inert failure described above for a committed config file.
/// <c>owner</c> comes from <see cref="Factory.Core.FactoryConfig.Name"/>, so that value must be
/// unique per machine wherever the backlog is shared.</remarks>
public class BeadsCli(string workingDirectory, string owner)
{
    private readonly Dictionary<string, string> _environment =
        new() { ["BD_NON_INTERACTIVE"] = "1", ["BEADS_NODE_ID"] = owner };

    /// <summary>Runs <c>bd</c>. Overridable so a test can interpose at a specific call — the window
    /// between the two writes that file a non-Ready item is bounded by one process start, so nothing
    /// outside this seam can reach it deterministically.</summary>
    public virtual ShellResult Exec(params string[] args) =>
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
