using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
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

    /// <summary>The commit SHA this baseline was captured against, as reported by <see
    /// cref="IRepoStateProvider"/> at capture time. Not wired up to any staleness comparison —
    /// <see cref="Commit"/> remains the source of truth for that.</summary>
    public string CapturedCommitSha { get; init; } = "";

    /// <summary>How many attempts each check needed. <see cref="RunAsync"/> already measures this and
    /// the capture used to discard it, at the one place where confidence decides whether a gate stays
    /// on. Absent from baselines written before it was recorded, which load as an empty map.</summary>
    public Dictionary<string, int> Attempts { get; init; } = [];

    /// <summary>Checks this baseline records as already failing. Only regressions block, so a check
    /// believed broken can no longer fail anything: naming these is naming the gates that are off.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> DisabledGates =>
        [.. Passing.Where(p => !p.Value).Select(p => p.Key).Order()];

    /// <summary>Checks that passed, but only after a retry — the machine decided the first attempt,
    /// not the code. The gate stays on; the evidence that this host is unreliable does not vanish.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> FlakyChecks =>
        [.. Passing.Where(p => p.Value && Attempts.GetValueOrDefault(p.Key, 1) > 1).Select(p => p.Key).Order()];

    public static ToolchainBaseline From(string commit, IReadOnlyList<CheckOutcome> results) => new()
    {
        Commit = commit,
        CapturedCommitSha = commit,
        Passing = results.ToDictionary(r => r.Name, r => r.Passed),
        Attempts = results.ToDictionary(r => r.Name, r => r.Attempts)
    };
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

/// <summary>The SDK version(s) and target framework(s) a repository declares it needs, read
/// from its own manifests (e.g. global.json, csproj) rather than assumed.</summary>
public sealed record RepoToolchainRequirement(
    IReadOnlyList<string> RequiredSdkVersions,
    IReadOnlyList<string> TargetFrameworks);

/// <summary>Reads the toolchain a repository declares it needs. Concerned only with the
/// repo's own manifests — never with what is actually installed on the host.</summary>
public interface IToolchainRequirementReader
{
    Task<RepoToolchainRequirement> ReadRequirementsAsync(string repoPath, CancellationToken ct = default);
}

/// <summary>Reads a dotnet repo's own declared requirements: the SDK pinned by global.json,
/// and the target framework(s) declared by its csproj files. Only ever reads the paths it is
/// given, so it stays fakeable in tests and never depends on what happens to be installed.</summary>
public sealed class DotnetToolchainRequirementReader : IToolchainRequirementReader
{
    public Task<RepoToolchainRequirement> ReadRequirementsAsync(string repoPath, CancellationToken ct = default)
    {
        var sdkVersions = ReadSdkVersions(repoPath);
        var targetFrameworks = ReadTargetFrameworks(repoPath);

        return Task.FromResult(new RepoToolchainRequirement(sdkVersions, targetFrameworks));
    }

    private static IReadOnlyList<string> ReadSdkVersions(string repoPath)
    {
        var globalJsonPath = Path.Combine(repoPath, "global.json");
        if (!File.Exists(globalJsonPath)) return [];

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(globalJsonPath));
            if (doc.RootElement.TryGetProperty("sdk", out var sdk) &&
                sdk.TryGetProperty("version", out var version) &&
                version.ValueKind == JsonValueKind.String &&
                version.GetString() is { Length: > 0 } pinned)
                return [pinned];
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }

        return [];
    }

    private static List<string> ReadTargetFrameworks(string repoPath)
    {
        var frameworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> csprojFiles;
        try
        {
            csprojFiles = Directory.EnumerateFiles(repoPath, "*.csproj", SearchOption.AllDirectories);
        }
        catch (IOException) { return []; }

        foreach (var csproj in csprojFiles)
        {
            try
            {
                var doc = XDocument.Load(csproj);
                foreach (var element in doc.Descendants("TargetFramework"))
                    if (!string.IsNullOrWhiteSpace(element.Value)) frameworks.Add(element.Value.Trim());

                foreach (var element in doc.Descendants("TargetFrameworks"))
                    foreach (var tfm in element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        frameworks.Add(tfm);
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException) { }
        }

        return frameworks.ToList();
    }
}

/// <summary>Reports the SDK/toolchain versions installed on the host. Injectable so
/// compatibility checks can be tested against a fake instead of shelling to a real toolchain.</summary>
public interface IInstalledSdkProvider
{
    Task<IReadOnlyList<string>> GetInstalledVersionsAsync(CancellationToken ct = default);
}

/// <summary>Whether a repo's declared toolchain requirement is satisfied by what is installed.
/// Installed/required versions are only carried when incompatible — a compatible result has
/// nothing to report.</summary>
public sealed record ToolchainCompatibilityResult
{
    public required bool IsCompatible { get; init; }
    public IReadOnlyList<string> RequiredVersions { get; init; } = [];
    public IReadOnlyList<string> InstalledVersions { get; init; } = [];

    public static ToolchainCompatibilityResult Compatible() => new() { IsCompatible = true };

    public static ToolchainCompatibilityResult Incompatible(
        IReadOnlyList<string> required, IReadOnlyList<string> installed) => new()
        {
            IsCompatible = false,
            RequiredVersions = required,
            InstalledVersions = installed
        };
}

/// <summary>Determines whether a repo's toolchain requirement is met by the installed SDKs.</summary>
public interface IToolchainProbe
{
    Task<ToolchainCompatibilityResult> ProbeAsync(string repoPath, CancellationToken ct = default);
}

/// <summary>A toolchain incompatibility, distinct from a station's generic <see
/// cref="StationResult.Failed"/>: it carries the exact required and installed versions so the
/// message can say precisely what is missing.</summary>
public sealed record ToolchainMismatch(IReadOnlyList<string> RequiredVersions, IReadOnlyList<string> InstalledVersions)
{
    public string Message =>
        $"requires SDK {string.Join(", ", RequiredVersions)} but found {string.Join(", ", InstalledVersions)} installed";
}

/// <summary>Reports installed SDKs by shelling to `dotnet --list-sdks`, the same host the
/// factory itself runs on.</summary>
public sealed class DotnetInstalledSdkProvider : IInstalledSdkProvider
{
    public async Task<IReadOnlyList<string>> GetInstalledVersionsAsync(CancellationToken ct = default)
    {
        if (!Shell.Which("dotnet")) return [];

        var result = await Shell.RunAsync("dotnet --list-sdks", Directory.GetCurrentDirectory(), 60, ct)
            .ConfigureAwait(false);
        if (!result.Ok) return [];

        return result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' ')[0].Trim())
            .Where(v => v.Length > 0)
            .ToList();
    }
}

/// <summary>Compares dotnet SDK versions using the same roll-forward semantics the `dotnet`
/// host itself applies when no explicit `rollForward` policy is pinned in global.json
/// (`latestPatch`): an installed SDK satisfies a requirement only when its major, minor, and
/// feature band (the version's hundreds-digit, e.g. 100 for "9.0.1xx") match, and its patch is
/// greater than or equal to the one required. A "9.0.100" pin is satisfied by an installed
/// "9.0.108" but not by "9.0.200" (a different feature band) or "10.0.100" (a different
/// major).</summary>
public static class DotnetSdkRollForwardComparer
{
    public static bool IsCompatible(string requiredVersion, string installedVersion)
    {
        if (!TryParse(requiredVersion, out var required) || !TryParse(installedVersion, out var installed))
            return requiredVersion == installedVersion;

        return required.Major == installed.Major
            && required.Minor == installed.Minor
            && required.FeatureBand == installed.FeatureBand
            && installed.Patch >= required.Patch;
    }

    private readonly record struct SdkVersion(int Major, int Minor, int Patch)
    {
        public int FeatureBand => Patch / 100 * 100;
    }

    private static bool TryParse(string version, out SdkVersion parsed)
    {
        parsed = default;
        var parts = version.Split('.');
        if (parts.Length < 3) return false;
        if (!int.TryParse(parts[0], out var major)) return false;
        if (!int.TryParse(parts[1], out var minor)) return false;

        var patchDigits = new string(parts[2].TakeWhile(char.IsDigit).ToArray());
        if (patchDigits.Length == 0 || !int.TryParse(patchDigits, out var patch)) return false;

        parsed = new SdkVersion(major, minor, patch);
        return true;
    }
}

/// <summary>The default toolchain probe: compares the SDK version(s) a dotnet repo pins in
/// global.json against what is actually installed, using .NET's own roll-forward semantics.
/// The only probe the factory ships with, since dotnet is the toolchain it runs on itself;
/// other ecosystems can plug in their own <see cref="IToolchainRequirementReader"/> and <see
/// cref="IInstalledSdkProvider"/> pair — via a new <see cref="IToolchainProbe"/> — without
/// touching how the factory selects or invokes a probe.</summary>
public sealed class DotnetToolchainProbe(
    IToolchainRequirementReader? requirementReader = null,
    IInstalledSdkProvider? installedSdkProvider = null,
    Func<string, string, bool>? versionComparer = null) : IToolchainProbe
{
    private readonly IToolchainRequirementReader _requirementReader =
        requirementReader ?? new DotnetToolchainRequirementReader();
    private readonly IInstalledSdkProvider _installedSdkProvider =
        installedSdkProvider ?? new DotnetInstalledSdkProvider();
    private readonly Func<string, string, bool> _versionComparer =
        versionComparer ?? DotnetSdkRollForwardComparer.IsCompatible;

    public async Task<ToolchainCompatibilityResult> ProbeAsync(string repoPath, CancellationToken ct = default)
    {
        var requirement = await _requirementReader.ReadRequirementsAsync(repoPath, ct).ConfigureAwait(false);
        if (requirement.RequiredSdkVersions.Count == 0) return ToolchainCompatibilityResult.Compatible();

        var installed = await _installedSdkProvider.GetInstalledVersionsAsync(ct).ConfigureAwait(false);

        var compatible = requirement.RequiredSdkVersions.All(required =>
            installed.Any(i => _versionComparer(required, i)));

        return compatible
            ? ToolchainCompatibilityResult.Compatible()
            : ToolchainCompatibilityResult.Incompatible(requirement.RequiredSdkVersions, installed);
    }
}

/// <summary>Named, selectable toolchain probes — one per ecosystem. Adding support for a new
/// ecosystem (Node, Python, ...) means adding a factory method here that wires up that
/// ecosystem's own <see cref="IToolchainRequirementReader"/> and <see
/// cref="IInstalledSdkProvider"/>, never changing how a station picks or calls a probe.</summary>
public static class ToolchainProbes
{
    public static IToolchainProbe ForDotnet() => new DotnetToolchainProbe();
}

/// <summary>Serialises every toolchain invocation across one factory instance — each item's own
/// check and every baseline capture or recapture — so concurrency above 1 never puts two
/// compiles on the host's shared MSBuild node-reuse processes and Roslyn compiler server at
/// once. <see cref="ToolchainRunner.BaselineAsync"/> documents why a baseline taken while
/// something else is building is unreliable; this gate is what keeps that invariant true once
/// items run concurrently. Model calls are never serialised by this — only the toolchain.</summary>
public sealed class ToolchainGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        return new Release(_gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
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

    /// <summary>Reads whatever baseline is cached, regardless of which commit it was captured
    /// against — staleness against the current commit is judged separately, by <see
    /// cref="GetOrRecaptureBaselineAsync"/>.</summary>
    public static ToolchainBaseline? TryLoadBaseline(string cachePath)
    {
        if (!File.Exists(cachePath)) return null;
        try
        {
            return FactoryJson.Read<ToolchainBaseline>(File.ReadAllText(cachePath));
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
        Toolchain toolchain, string repoRoot, string cachePath, IRepoStateProvider repoStateProvider,
        CancellationToken ct = default)
    {
        var commit = await repoStateProvider.GetCurrentMasterShaAsync(ct).ConfigureAwait(false);

        if (TryLoadBaseline(cachePath, commit) is { } cached) return cached;

        var results = await RunAsync(toolchain, repoRoot, ct).ConfigureAwait(false);
        var baseline = ToolchainBaseline.From(commit, results);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
            await File.WriteAllTextAsync(cachePath, FactoryJson.Write(baseline, pretty: true), ct)
                      .ConfigureAwait(false);
        }
        catch (IOException) { }

        return baseline;
    }

    /// <summary>
    /// Reuses a cached baseline only if it was captured against the commit the repo is
    /// currently on. A baseline whose commit has since moved is stale whether the move was the
    /// factory's own commit or an external push — either way it no longer describes the
    /// mainline being checked against, so it is discarded and recaptured rather than trusted.
    /// </summary>
    public static async Task<ToolchainBaseline> GetOrRecaptureBaselineAsync(
        ToolchainBaseline cached, Toolchain toolchain, string repoRoot, string cachePath,
        IRepoStateProvider repoStateProvider, CancellationToken ct = default,
        Func<ToolchainCheck, string, CancellationToken, Task<ShellResult>>? execute = null)
    {
        var currentSha = await repoStateProvider.GetCurrentMasterShaAsync(ct).ConfigureAwait(false);
        if (cached.Commit == currentSha) return cached;

        var results = await RunAsync(toolchain, repoRoot, ct, execute).ConfigureAwait(false);
        var fresh = ToolchainBaseline.From(currentSha, results);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
            await File.WriteAllTextAsync(cachePath, FactoryJson.Write(fresh, pretty: true), ct)
                      .ConfigureAwait(false);
        }
        catch (IOException) { }

        return fresh;
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
