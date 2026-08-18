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
    Func<string, CancellationToken, Task<ToolchainCompatibility>>? checkCompatibility = null) : IStation
{
    private readonly Func<string, CancellationToken, Task<ToolchainCompatibility>> _checkCompatibility =
        checkCompatibility ?? ToolchainRunner.CheckCompatibilityAsync;

    public StationRole Role => StationRole.Check;

    public async Task<StationResult> ExecuteAsync(StationContext ctx)
    {
        var s = ctx.Services;

        // Compatibility is checked regardless of what toolchain is detected: a pinned SDK
        // version can be wrong even before there is anything to build.
        var compatibility = await _checkCompatibility(ctx.Run.WorkDir, ctx.Ct).ConfigureAwait(false);
        if (!compatibility.Compatible)
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

        // The baseline is taken once before dispatch, while nothing else is building. If none
        // was recorded, assume everything was healthy: absent evidence that a check was
        // already broken, a failure is attributed to the change rather than excused.
        var commit = await ToolchainRunner.HeadCommitAsync(ctx.Services.Workspace.RepoRoot, ctx.Ct)
            .ConfigureAwait(false);
        var baseline = ToolchainRunner.TryLoadBaseline(ctx.Services.Paths.BaselineFile, commit)
                       ?? new ToolchainBaseline { Commit = commit };

        var results = await ToolchainRunner.RunAsync(toolchain, ctx.Run.WorkDir, ctx.Ct).ConfigureAwait(false);
        var verdict = ToolchainRunner.Compare(results, baseline);

        var elapsed = results.Sum(r => r.DurationMs);
        ctx.Log($"{toolchain.Name}: {verdict.Summary} — {elapsed / 1000.0:F0}s, zero tokens");

        foreach (var pre in verdict.PreExisting)
            ctx.Log($"  {pre.Name} was already failing before this work; not counted against it");

        return verdict.Passed
            ? StationResult.Ok(verdict.Summary)
            : StationResult.GateFailed(string.Join("\n", verdict.Regressions.Select(r => r.Detail)));
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
        StationContext ctx, ToolchainCompatibility mismatch)
    {
        ctx.Log($"toolchain mismatch: requires {mismatch.Required}, found {mismatch.Installed ?? "none"}");

        var requirement = new ToolchainRequirement("dotnet", mismatch.Required);
        var remediation = await remediationRunner.RemediateAsync(requirement, ctx.Ct).ConfigureAwait(false);

        var recheck = remediation.Found
            ? await _checkCompatibility(ctx.Run.WorkDir, ctx.Ct).ConfigureAwait(false)
            : mismatch;

        if (remediation.Found && remediation.Succeeded && recheck.Compatible)
        {
            ctx.Log("remediation resolved the toolchain mismatch; proceeding");
            return null;
        }

        var attempted = remediation.Found ? "remediation attempted and failed" : "no remediation available";
        var reason = $"requires {mismatch.Required}, {recheck.Installed ?? "none"} installed; {attempted}";
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
        FactoryServices services, CancellationToken ct = default)
    {
        var toolchain = Toolchain.Detect(services.Workspace.RepoRoot);
        if (toolchain.IsEmpty) return null;

        var commit = await ToolchainRunner.HeadCommitAsync(services.Workspace.RepoRoot, ct).ConfigureAwait(false);
        if (ToolchainRunner.TryLoadBaseline(services.Paths.BaselineFile, commit) is { } cached) return cached;

        services.Log($"  [check] baselining {toolchain.Describe} on the mainline…");

        var baseline = await ToolchainRunner.BaselineAsync(
            toolchain, services.Workspace.RepoRoot, services.Paths.BaselineFile, ct).ConfigureAwait(false);

        // A baseline that cannot establish a healthy mainline is worth saying out loud: from
        // here on, those checks stop blocking anything.
        foreach (var (name, passing) in baseline.Passing.Where(p => !p.Value))
            services.Log($"  [check] warning: `{name}` already fails on the mainline — " +
                         "it will not block work until it is fixed");

        return baseline;
    }
}
