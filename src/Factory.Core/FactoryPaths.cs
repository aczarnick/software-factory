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

    /// <summary>Observed model usage windows, so a restart inside an exhausted window does
    /// not immediately spend its way back into the same rejection.</summary>
    public string UsageFile => Path.Combine(Root, "usage.json");

    /// <summary>Which toolchain checks passed on the mainline, keyed by commit.</summary>
    public string BaselineFile => Path.Combine(Root, "baseline.json");

    public bool Exists => File.Exists(Config);

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(PromptsDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(RunsDir);
        Directory.CreateDirectory(WorktreesDir);
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
}
