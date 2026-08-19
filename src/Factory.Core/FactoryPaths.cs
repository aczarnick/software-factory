namespace Factory.Core;

/// <summary>Canonical layout of a deployed factory. Everything a factory owns lives under
/// a single <c>.factory</c> directory inside the codebase it works on, so deploying is
/// copying a directory and removing one is deleting it.</summary>
public sealed class FactoryPaths(string repoRoot)
{
    public const string DirName = ".factory";

    public string RepoRoot { get; } = Path.GetFullPath(repoRoot);
    public string Root => Path.Combine(RepoRoot, DirName);

    public string Config => Path.Combine(Root, "factory.json");
    public string BlueprintFile => Path.Combine(Root, "blueprint.json");
    public string LedgerFile => Path.Combine(Root, "ledger.jsonl");
    public string PromptsDir => Path.Combine(Root, "prompts");
    public string CacheDir => Path.Combine(Root, "cache");
    public string RunsDir => Path.Combine(Root, "runs");
    public string WorktreesDir => Path.Combine(Root, "worktrees");
    public string LockFile => Path.Combine(Root, "factory.lock");

    /// <summary>Third-party provider assemblies, loaded at open.</summary>
    public string PluginsDir => Path.Combine(Root, "plugins");

    /// <summary>Observed model usage windows, so a restart inside an exhausted window does
    /// not immediately spend its way back into the same rejection.</summary>
    public string UsageFile => Path.Combine(Root, "usage.json");

    /// <summary>Which toolchain checks passed on the mainline, keyed by commit.</summary>
    public string BaselineFile => Path.Combine(Root, "baseline.json");

    /// <summary>Point-in-time snapshot of the running factory, for external readers.</summary>
    public string StatusFile => Path.Combine(Root, "status.json");

    public bool Exists => File.Exists(Config);

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(PromptsDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(RunsDir);
        Directory.CreateDirectory(WorktreesDir);
        Directory.CreateDirectory(PluginsDir);
    }

    /// <summary>Walks up from a directory to find the nearest deployed factory.</summary>
    public static FactoryPaths? Discover(string start)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, DirName, "factory.json")))
                return new FactoryPaths(dir.FullName);
            dir = dir.Parent;
        }
        return null;
    }
}

/// <summary>On-disk factory configuration.</summary>
public sealed record FactoryConfig
{
    /// <summary>This checkout's identity. Defaults to the repo directory's basename, which is
    /// <em>not</em> machine-unique — two clones of the same repo into identically named
    /// directories get the same default. That is harmless with the <c>ledger</c> backlog, but with
    /// the <c>beads</c> provider this value doubles as both the claim assignee and the cross-replica
    /// node id (see the beads provider's <c>BeadsCli</c>), so wherever a beads backlog is shared
    /// across machines, <c>Name</c> must be set explicitly and distinctly per machine — an empty
    /// value is read as unset and silently disarms the guard, same as leaving it unconfigured.</summary>
    public required string Name { get; init; }
    public string BlueprintName { get; init; } = "standard";
    public int MaxConcurrency { get; init; } = 2;
    public int PollSeconds { get; init; } = 10;

    /// <summary>Child factories linked into this one: name -> path (absolute or relative).</summary>
    public Dictionary<string, string> Factories { get; init; } = [];

    /// <summary>Turn the self-improvement loop on or off.</summary>
    public bool EvolutionEnabled { get; init; } = true;

    /// <summary>Runs between evolution passes.</summary>
    public int EvolveEveryRuns { get; init; } = 20;

    public string CreatedAt { get; init; } = DateTimeOffset.UtcNow.ToString("O");

    /// <summary>Backlog provider. Exactly one is active.</summary>
    public ProviderRef WorkItemStore { get; init; } = new("ledger");

    public RunHistoryConfig RunHistory { get; init; } = new();
}
