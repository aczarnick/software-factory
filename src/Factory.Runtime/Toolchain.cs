using System.Text.Json;
using Factory.Core;

namespace Factory.Runtime;

public sealed record ToolchainCheck(string Name, string Command, int TimeoutSeconds = 600);

/// <summary>
/// The deterministic checks a repository already knows how to run — its compiler, its test
/// suite, its linter, its formatter.
///
/// These matter more than model-authored acceptance criteria, because they are not authored
/// by the thing being checked. An agent that writes its own criteria can write weak ones; it
/// cannot argue with a compiler. Detection probes for the tool as well as the manifest, so a
/// check is only declared when it can actually run.
/// </summary>
public sealed record Toolchain
{
    public required string Name { get; init; }
    public IReadOnlyList<ToolchainCheck> Checks { get; init; } = [];

    public bool IsEmpty => Checks.Count == 0;

    public string Describe => IsEmpty
        ? "none detected"
        : $"{Name}: {string.Join(", ", Checks.Select(c => c.Name))}";

    public static readonly Toolchain None = new() { Name = "none" };

    public static Toolchain Detect(string root)
    {
        if (Exists(root, "*.sln") || Exists(root, "*.csproj", recursive: true)) return Dotnet(root);
        if (File.Exists(Path.Combine(root, "Cargo.toml"))) return Rust();
        if (File.Exists(Path.Combine(root, "go.mod"))) return Go();
        if (File.Exists(Path.Combine(root, "package.json"))) return Node(root);
        if (File.Exists(Path.Combine(root, "pyproject.toml")) ||
            File.Exists(Path.Combine(root, "requirements.txt")) ||
            Exists(root, "*.py")) return Python(root);

        return None;
    }

    private static bool Exists(string root, string pattern, bool recursive = false)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Any();
        }
        catch (IOException) { return false; }
    }

    private static bool OnPath(string tool) => Shell.Which(tool);

    private static Toolchain Dotnet(string root)
    {
        var checks = new List<ToolchainCheck>
        {
            new("build", "dotnet build --nologo -v quiet", 900)
        };

        if (Exists(root, "*Test*.csproj", recursive: true) || Exists(root, "*Tests*.csproj", recursive: true))
            checks.Add(new ToolchainCheck("test", "dotnet test --nologo -v quiet", 1800));

        return new Toolchain { Name = "dotnet", Checks = checks };
    }

    private static Toolchain Rust() => new()
    {
        Name = "rust",
        Checks =
        [
            new("build", "cargo build --quiet", 900),
            new("test", "cargo test --quiet", 1800),
            new("lint", "cargo clippy --quiet -- -D warnings", 900),
            new("format", "cargo fmt --check", 120)
        ]
    };

    private static Toolchain Go() => new()
    {
        Name = "go",
        Checks =
        [
            new("build", "go build ./...", 600),
            new("test", "go test ./...", 1800),
            new("vet", "go vet ./...", 600)
        ]
    };

    /// <summary>Node projects declare their own checks; the package manifest is the source of
    /// truth rather than a guess about which framework is in use.</summary>
    private static Toolchain Node(string root)
    {
        var scripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "package.json")));
            if (doc.RootElement.TryGetProperty("scripts", out var s) && s.ValueKind == JsonValueKind.Object)
                foreach (var prop in s.EnumerateObject()) scripts.Add(prop.Name);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }

        var checks = new List<ToolchainCheck>();
        if (scripts.Contains("build")) checks.Add(new ToolchainCheck("build", "npm run build", 900));
        if (scripts.Contains("typecheck")) checks.Add(new ToolchainCheck("typecheck", "npm run typecheck", 600));
        if (scripts.Contains("lint")) checks.Add(new ToolchainCheck("lint", "npm run lint", 600));
        if (scripts.Contains("test")) checks.Add(new ToolchainCheck("test", "npm test", 1800));

        return new Toolchain { Name = "node", Checks = checks };
    }

    private static Toolchain Python(string root)
    {
        var checks = new List<ToolchainCheck>
        {
            // Compiles every module: catches syntax errors with no project config at all.
            new("syntax", "python3 -m compileall -q .", 300)
        };

        if (OnPath("ruff")) checks.Add(new ToolchainCheck("lint", "ruff check .", 300));

        var hasTests = Exists(root, "test_*.py", recursive: true) ||
                       Exists(root, "*_test.py", recursive: true) ||
                       Directory.Exists(Path.Combine(root, "tests"));
        if (hasTests && OnPath("pytest")) checks.Add(new ToolchainCheck("test", "pytest -q", 1800));

        return new Toolchain { Name = "python", Checks = checks };
    }
}

/// <summary>Whether the toolchain actually installed satisfies what the repository pins.
/// Cheap enough to run twice: once to detect a mismatch, once to confirm a remediation fixed
/// it, without paying for a full build either time.</summary>
public sealed record ToolchainCompatibility(bool Compatible, string? Required, string? Installed)
{
    public static readonly ToolchainCompatibility Ok = new(true, null, null);
}

public sealed record CheckOutcome(string Name, bool Passed, string Detail, long DurationMs, int Attempts = 1)
{
    /// <summary>True when the check only succeeded on a retry — worth surfacing, because a
    /// gate that is intermittently wrong is worse than one that is consistently strict.</summary>
    public bool WasFlaky => Passed && Attempts > 1;
}

/// <summary>Which checks passed on the mainline before the factory touched anything.</summary>
public sealed record ToolchainBaseline
{
    public string Commit { get; init; } = "";
    public Dictionary<string, bool> Passing { get; init; } = [];
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ToolchainVerdict(
    IReadOnlyList<CheckOutcome> Results,
    IReadOnlyList<CheckOutcome> Regressions,
    IReadOnlyList<CheckOutcome> PreExisting)
{
    /// <summary>Only regressions block. A repository that arrived with a failing linter is
    /// not the item's fault, and failing every item over it would make the factory unusable
    /// on real codebases.</summary>
    public bool Passed => Regressions.Count == 0;

    public string Summary
    {
        get
        {
            if (Results.Count == 0) return "no toolchain checks detected";
            var passed = Results.Count(r => r.Passed);
            var note = PreExisting.Count > 0
                ? $" ({PreExisting.Count} already failing before this work)"
                : "";
            return Passed
                ? $"{passed}/{Results.Count} toolchain checks passed{note}"
                : "broke " + string.Join(", ", Regressions.Select(r => r.Name)) + $": {Regressions[0].Detail}";
        }
    }
}

public static class ToolchainRunner
{
    /// <summary>
    /// Runs the toolchain, retrying a failed check once.
    ///
    /// Build tools are not perfectly reliable — the Roslyn compiler server has been observed
    /// dying outright (`csc.dll exited with code 132`) on roughly half of builds on some
    /// hosts. A gate that reports a spurious failure is worse than no gate: it blames the work
    /// for the machine's flakiness, and during baseline capture it does the opposite, recording
    /// a check as already-broken and silently switching the gate off. A check that fails twice
    /// is believed; a check that fails once and then passes is recorded as flaky.
    /// </summary>
    public static async Task<IReadOnlyList<CheckOutcome>> RunAsync(
        Toolchain toolchain, string workDir, CancellationToken ct = default,
        Func<ToolchainCheck, string, CancellationToken, Task<ShellResult>>? execute = null)
    {
        var run = execute ?? ((c, dir, token) => Shell.RunAsync(c.Command, dir, c.TimeoutSeconds, token));
        var results = new List<CheckOutcome>();

        foreach (var check in toolchain.Checks)
        {
            ct.ThrowIfCancellationRequested();

            var started = DateTimeOffset.UtcNow;
            var attempts = 1;
            var result = await run(check, workDir, ct).ConfigureAwait(false);

            if (!result.Ok)
            {
                attempts = 2;
                result = await run(check, workDir, ct).ConfigureAwait(false);
            }

            var elapsed = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;

            results.Add(new CheckOutcome(
                check.Name,
                result.Ok,
                result.Ok ? $"`{check.Command}` passed" : Explain(check, result),
                elapsed,
                attempts));

            // A failed build makes every later check meaningless, so stop and report the
            // thing that actually needs fixing rather than a cascade of consequences.
            if (!result.Ok && check.Name is "build" or "syntax") break;
        }

        return results;
    }

    private static string Explain(ToolchainCheck check, ShellResult run)
    {
        if (run.TimedOut) return $"`{check.Command}` timed out after {check.TimeoutSeconds}s";

        // The tail is where compilers and test runners put the thing that went wrong, and
        // this text is fed straight back into the next implementation attempt.
        var output = run.Combined.Trim();
        const int max = 2500;
        if (output.Length > max) output = "…\n" + output[^max..];
        return $"`{check.Command}` exited {run.ExitCode}:\n{output}";
    }

    /// <summary>Checks the pinned SDK version against what is actually installed, without
    /// running a build. Only .NET's `global.json` is understood today, since that is the
    /// toolchain this factory itself runs on; a repository with no pin is always compatible.</summary>
    public static async Task<ToolchainCompatibility> CheckCompatibilityAsync(string root, CancellationToken ct = default)
    {
        var globalJson = Path.Combine(root, "global.json");
        if (!File.Exists(globalJson)) return ToolchainCompatibility.Ok;

        string? required;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(globalJson));
            required = doc.RootElement.TryGetProperty("sdk", out var sdk) &&
                       sdk.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return ToolchainCompatibility.Ok; }

        if (string.IsNullOrWhiteSpace(required)) return ToolchainCompatibility.Ok;
        if (!Shell.Which("dotnet")) return new ToolchainCompatibility(false, required, null);

        var result = await Shell.RunAsync("dotnet --version", root, 60, ct).ConfigureAwait(false);
        var installed = result.Ok ? result.Stdout.Trim() : null;

        // A pin of "9.0.100" is satisfied by any installed 9.x SDK; patch-level drift is not
        // a mismatch worth blocking on.
        var compatible = installed is not null && installed.Split('.')[0] == required.Split('.')[0];
        return new ToolchainCompatibility(compatible, required, installed);
    }

    public static async Task<string> HeadCommitAsync(string repoRoot, CancellationToken ct = default) =>
        (await Shell.GitAsync(repoRoot, ct, "rev-parse", "HEAD").ConfigureAwait(false)).Stdout.Trim();

    /// <summary>Reads a cached baseline if one was taken at this commit.</summary>
    public static ToolchainBaseline? TryLoadBaseline(string cachePath, string commit)
    {
        if (!File.Exists(cachePath) || commit.Length == 0) return null;
        try
        {
            var cached = FactoryJson.Read<ToolchainBaseline>(File.ReadAllText(cachePath));
            return cached is not null && cached.Commit == commit ? cached : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    /// <summary>
    /// Captures which checks pass on the mainline, cached against the commit.
    ///
    /// This must run when nothing else is building. Taken while implementation agents are
    /// compiling in their own worktrees, it contends for the same toolchain and can record a
    /// spurious failure — which silently downgrades the gate, because a check believed to be
    /// already broken no longer blocks anything.
    /// </summary>
    public static async Task<ToolchainBaseline> BaselineAsync(
        Toolchain toolchain, string repoRoot, string cachePath, CancellationToken ct = default)
    {
        var commit = await HeadCommitAsync(repoRoot, ct).ConfigureAwait(false);

        if (TryLoadBaseline(cachePath, commit) is { } cached) return cached;

        var results = await RunAsync(toolchain, repoRoot, ct).ConfigureAwait(false);
        var baseline = new ToolchainBaseline
        {
            Commit = commit,
            Passing = results.ToDictionary(r => r.Name, r => r.Passed)
        };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
            File.WriteAllText(cachePath, FactoryJson.Write(baseline, pretty: true));
        }
        catch (IOException) { }

        return baseline;
    }

    public static ToolchainVerdict Compare(IReadOnlyList<CheckOutcome> results, ToolchainBaseline baseline)
    {
        var regressions = new List<CheckOutcome>();
        var preExisting = new List<CheckOutcome>();

        foreach (var result in results.Where(r => !r.Passed))
        {
            // Unknown at baseline is treated as "was passing": a check that only exists
            // because of this work must pass, or the work has not been demonstrated.
            if (baseline.Passing.TryGetValue(result.Name, out var wasPassing) && !wasPassing)
                preExisting.Add(result);
            else
                regressions.Add(result);
        }

        return new ToolchainVerdict(results, regressions, preExisting);
    }
}
