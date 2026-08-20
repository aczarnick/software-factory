using Factory.Core;

namespace Factory.Runtime;

/// <summary>
/// Runs the repository's own toolchain against the item's worktree: compiler, tests, linter,
/// formatter. Costs no tokens and cannot be talked out of a failure.
///
/// This is a stronger guarantee than the acceptance criteria, because those are written by
/// the same kind of thing being checked. A station can author a criterion that its own work
/// happens to satisfy; it cannot author a compiler. Failures come back as the tool's own
/// output, which is fed verbatim into the next implementation attempt — the error text a
/// compiler emits is already the most useful possible description of what to fix.
/// </summary>
public sealed class CheckStation(
    IRemediationRunner remediationRunner,
    IToolchainProbe? probe = null,
    IRepoStateProvider? repoStateProvider = null,
    Func<ToolchainCheck, string, CancellationToken, Task<ShellResult>>? execute = null) : IStation
{
    private readonly IToolchainProbe _probe = probe ?? new DotnetToolchainProbe();
    private readonly IRepoStateProvider? _repoStateProvider = repoStateProvider;

    /// <summary>Overrides how a check is actually run. Only ever set by tests, so they can
    /// observe concurrency around a fake compile without shelling out to a real one.</summary>
    private readonly Func<ToolchainCheck, string, CancellationToken, Task<ShellResult>>? _execute = execute;

    public StationRole Role => StationRole.Check;

    public async Task<StationResult> ExecuteAsync(StationContext ctx)
    {
        var s = ctx.Services;

        // Compatibility is probed regardless of what toolchain is detected: a pinned SDK
        // version can be wrong even before there is anything to build.
        var compatibility = await _probe.ProbeAsync(ctx.Run.WorkDir, ctx.Ct).ConfigureAwait(false);
        if (!compatibility.IsCompatible)
        {
            var blocked = await ResolveMismatchAsync(ctx, compatibility).ConfigureAwait(false);
            if (blocked is not null) return blocked;
        }

        var toolchain = Toolchain.Detect(ctx.Run.WorkDir);

        if (toolchain.IsEmpty)
        {
            ctx.Log("no toolchain detected; relying on acceptance criteria alone");
            return StationResult.Ok("no toolchain detected");
        }

        var (baseline, results) = await RunCheckedAsync(ctx, toolchain).ConfigureAwait(false);
        var verdict = ToolchainRunner.Compare(results, baseline);

        var elapsed = results.Sum(r => r.DurationMs);
        ctx.Log($"{toolchain.Name}: {verdict.Summary} — {elapsed / 1000.0:F0}s, zero tokens");

        foreach (var pre in verdict.PreExisting)
            ctx.Log($"  {pre.Name} was already failing before this work; not counted against it");

        return verdict.Passed
            ? StationResult.Ok(verdict.Summary)
            : StationResult.GateFailed(string.Join("\n", verdict.Regressions.Select(r => r.Detail)));
    }

    /// <summary>Recaptures the baseline if it has gone stale and runs this item's own checks,
    /// both under the factory's shared <see cref="ToolchainGate"/> — so this item's compile
    /// never overlaps another item's compile, or a baseline recapture, on the same machine.</summary>
    private async Task<(ToolchainBaseline Baseline, IReadOnlyList<CheckOutcome> Results)> RunCheckedAsync(
        StationContext ctx, Toolchain toolchain)
    {
        using var _ = await ctx.Services.ToolchainGate.AcquireAsync(ctx.Ct).ConfigureAwait(false);

        // The baseline is taken once before dispatch, while nothing else is building. If the
        // repo has since moved to a different commit — whether from the factory's own work or
        // an external push — the cached baseline no longer describes the mainline being
        // checked against, so it is recaptured rather than trusted.
        var cached = ToolchainRunner.TryLoadBaseline(ctx.Services.Paths.BaselineFile) ?? new ToolchainBaseline();
        var repoState = _repoStateProvider ?? new GitRepoStateProvider(ctx.Services.Workspace.RepoRoot);
        var baseline = await ToolchainRunner.GetOrRecaptureBaselineAsync(
            cached, toolchain, ctx.Services.Workspace.RepoRoot, ctx.Services.Paths.BaselineFile,
            repoState, execute: _execute, ct: ctx.Ct).ConfigureAwait(false);

        var results = await ToolchainRunner.RunAsync(toolchain, ctx.Run.WorkDir, execute: _execute, ct: ctx.Ct).ConfigureAwait(false);
        return (baseline, results);
    }

    /// <summary>
    /// Attempts one bounded remediation for a toolchain version mismatch and re-checks
    /// compatibility only — never a full build — to see whether it worked. Returns null to let
    /// the normal build/check path proceed once the environment is confirmed to satisfy
    /// requirements; otherwise a Blocked result naming what is required, what is installed, and
    /// whether remediation was tried. There is no retry loop: a remediation gets exactly one
    /// attempt, because a script that fails once is not made more likely to succeed by running
    /// it again.
    /// </summary>
    private async Task<StationResult?> ResolveMismatchAsync(
        StationContext ctx, ToolchainCompatibilityResult mismatch)
    {
        var toolchainMismatch = new ToolchainMismatch(mismatch.RequiredVersions, mismatch.InstalledVersions);
        ctx.Log($"toolchain mismatch: {toolchainMismatch.Message}");

        var requirement = new ToolchainRequirement("dotnet", string.Join(", ", mismatch.RequiredVersions));
        var remediation = await remediationRunner.RemediateAsync(requirement, ctx.Ct).ConfigureAwait(false);

        var recheck = remediation.Found
            ? await _probe.ProbeAsync(ctx.Run.WorkDir, ctx.Ct).ConfigureAwait(false)
            : mismatch;

        if (remediation.Found && remediation.Succeeded && recheck.IsCompatible)
        {
            ctx.Log("remediation resolved the toolchain mismatch; proceeding");
            return null;
        }

        var attempted = remediation.Found ? "remediation attempted and failed" : "no remediation available";
        toolchainMismatch = new ToolchainMismatch(recheck.RequiredVersions, recheck.InstalledVersions);
        var reason = $"{toolchainMismatch.Message}; {attempted}";
        ctx.Log($"blocked: {reason}");

        return new StationResult
        {
            Success = true,
            GatePassed = false,
            Detail = reason,
            Item = ctx.Item with { State = WorkItemState.Blocked, LastError = reason }
        };
    }

    /// <summary>Establishes which checks passed before the factory touched anything, so an item
    /// is only blamed for what it broke. Called once by the orchestrator before any dispatch,
    /// because a baseline taken while agents are compiling is not a baseline.</summary>
    public static async Task<ToolchainBaseline?> CaptureBaselineAsync(
        FactoryServices services, IToolchainProbe? probe = null,
        IRepoStateProvider? repoStateProvider = null, CancellationToken ct = default)
    {
        var toolchain = Toolchain.Detect(services.Workspace.RepoRoot);
        if (toolchain.IsEmpty) return null;

        // A mismatched toolchain makes every check below fail for reasons that have nothing
        // to do with the mainline's health. Capturing that as a baseline would record it as
        // "already failing" and keep excusing real regressions even after the mismatch is fixed.
        var compatibility = await (probe ?? new DotnetToolchainProbe())
            .ProbeAsync(services.Workspace.RepoRoot, ct).ConfigureAwait(false);
        if (!compatibility.IsCompatible)
        {
            var mismatch = new ToolchainMismatch(compatibility.RequiredVersions, compatibility.InstalledVersions);
            services.Log($"  [check] toolchain mismatch: {mismatch.Message}; skipping baseline capture");
            return null;
        }

        var repoState = repoStateProvider ?? new GitRepoStateProvider(services.Workspace.RepoRoot);
        var commit = await repoState.GetCurrentMasterShaAsync(ct).ConfigureAwait(false);
        if (ToolchainRunner.TryLoadBaseline(services.Paths.BaselineFile, commit) is { } cached) return cached;

        services.Log($"  [check] baselining {toolchain.Describe} on the mainline…");

        // Shares the factory's ToolchainGate with every per-item check, so a recapture here
        // never overlaps a concurrent item's own compile.
        using var _ = await services.ToolchainGate.AcquireAsync(ct).ConfigureAwait(false);
        var baseline = await ToolchainRunner.BaselineAsync(
            toolchain, services.Workspace.RepoRoot, services.Paths.BaselineFile, repoState, ct).ConfigureAwait(false);

        // A baseline that cannot establish a healthy mainline is worth saying out loud: from
        // here on, those checks stop blocking anything.
        foreach (var (name, passing) in baseline.Passing.Where(p => !p.Value))
            services.Log($"  [check] warning: `{name}` already fails on the mainline — " +
                         "it will not block work until it is fixed");

        return baseline;
    }
}
