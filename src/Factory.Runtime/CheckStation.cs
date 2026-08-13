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
public sealed class CheckStation : IStation
{
    // Baseline capture builds the mainline, so only one item may do it at a time.
    private static readonly SemaphoreSlim BaselineGate = new(1, 1);

    public StationRole Role => StationRole.Check;

    public async Task<StationResult> ExecuteAsync(StationContext ctx)
    {
        var s = ctx.Services;
        var toolchain = Toolchain.Detect(ctx.Run.WorkDir);

        if (toolchain.IsEmpty)
        {
            ctx.Log("no toolchain detected; relying on acceptance criteria alone");
            return StationResult.Ok("no toolchain detected");
        }

        var baseline = await CaptureBaselineAsync(ctx, toolchain).ConfigureAwait(false);

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

    /// <summary>Establishes which checks passed before the factory touched anything, so an
    /// item is only blamed for what it broke. Cached against the commit it was taken at.</summary>
    private static async Task<ToolchainBaseline> CaptureBaselineAsync(StationContext ctx, Toolchain toolchain)
    {
        var cachePath = Path.Combine(ctx.Services.Paths.Root, "baseline.json");

        await BaselineGate.WaitAsync(ctx.Ct).ConfigureAwait(false);
        try
        {
            return await ToolchainRunner.BaselineAsync(
                toolchain, ctx.Services.Workspace.RepoRoot, cachePath, ctx.Ct).ConfigureAwait(false);
        }
        finally
        {
            BaselineGate.Release();
        }
    }
}
