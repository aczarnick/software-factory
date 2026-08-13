namespace Factory.Cli;

/// <summary>Minimal argument parser. Deliberately dependency-free: the factory must install
/// and run from a single self-contained binary.</summary>
public sealed class CommandLine
{
    public string Command { get; }
    public IReadOnlyList<string> Positional { get; }
    private readonly Dictionary<string, string?> _flags;

    private CommandLine(string command, List<string> positional, Dictionary<string, string?> flags)
    {
        Command = command;
        Positional = positional;
        _flags = flags;
    }

    public static CommandLine Parse(string[] args)
    {
        var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "help";
        var rest = args.Skip(command == "help" && args.Length > 0 && !args[0].StartsWith('-') ? 1 : 0).ToArray();
        if (command != "help") rest = args.Skip(1).ToArray();

        var positional = new List<string>();
        var flags = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < rest.Length; i++)
        {
            var token = rest[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(token);
                continue;
            }

            var name = token[2..];
            if (name.Contains('='))
            {
                var split = name.Split('=', 2);
                flags[split[0]] = split[1];
                continue;
            }

            var hasValue = i + 1 < rest.Length && !rest[i + 1].StartsWith("--", StringComparison.Ordinal);
            flags[name] = hasValue ? rest[++i] : null;
        }

        return new CommandLine(command, positional, flags);
    }

    public bool Has(string name) => _flags.ContainsKey(name);
    public string? Get(string name) => _flags.GetValueOrDefault(name);
    public string Get(string name, string fallback) => _flags.GetValueOrDefault(name) ?? fallback;

    public int? Int(string name) =>
        _flags.TryGetValue(name, out var v) && int.TryParse(v, out var n) ? n : null;

    public decimal? Decimal(string name) =>
        _flags.TryGetValue(name, out var v) && decimal.TryParse(v, out var n) ? n : null;

    public string? First => Positional.Count > 0 ? Positional[0] : null;

    /// <summary>Rejoins positional arguments, so an unquoted prompt still works.</summary>
    public string PositionalText => string.Join(" ", Positional);
}
