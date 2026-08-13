using Factory.Core;

namespace Factory.Runtime;

/// <summary>
/// Runs a work item inside a linked child factory.
///
/// This is what makes factories compose. To the parent pipeline a delegate is an ordinary
/// station; internally it is an entire factory with its own blueprint, prompts, ledger, and
/// budget. Because a factory can appear as a station, a factory of factories is just a
/// factory — the composition is closed under nesting, bounded only by the depth limit that
/// stops a factory containing itself from running away.
/// </summary>
public sealed class DelegateStation : IStation
{
    public StationRole Role => StationRole.Delegate;

    public async Task<StationResult> ExecuteAsync(StationContext ctx)
    {
        var s = ctx.Services;
        var childName = ctx.Def.DelegateTo
            ?? throw new InvalidOperationException($"Station '{ctx.Def.Id}' has no delegateTo.");

        if (ctx.Run.Depth >= s.Blueprint.MaxDelegationDepth)
            return StationResult.Failed(
                $"delegation depth limit ({s.Blueprint.MaxDelegationDepth}) reached at '{childName}'");

        if (!s.Blueprint.Factories.TryGetValue(childName, out var childPath))
            return StationResult.Failed($"no linked factory named '{childName}'");

        var resolved = Path.IsPathRooted(childPath)
            ? childPath
            : Path.GetFullPath(Path.Combine(s.Paths.RepoRoot, childPath));

        if (!Directory.Exists(resolved))
            return StationResult.Failed($"linked factory '{childName}' not found at {resolved}");

        s.Record(new DelegationStarted(ctx.Item.Id, childName, ctx.Run.Depth + 1));
        ctx.Log($"delegating to '{childName}' at {resolved}");

        // The child inherits the parent's transport. A composite configured with a particular
        // transport must not silently fall back to the default one inside its children.
        using var child = FactoryHost.Open(
            resolved, msg => s.Log($"    ({childName}) {msg}"), transport: s.Transport);

        // The item crosses the port as a fresh item in the child's own ledger: each factory
        // keeps an independent, auditable history of what it was asked to do.
        var forwarded = child.Submit(ctx.Item with
        {
            Id = Ids.New("wi"),
            ParentId = null,
            State = WorkItemState.Draft,
            Station = null,
            Attempts = 0,
            SpentUsd = 0m,
            Provenance = Provenance.FromAgent($"{s.Config.Name}/{ctx.Def.Id}")
        });

        var report = await child.CreateOrchestrator().RunAsync(new OrchestratorOptions
        {
            StopWhenIdle = true,
            Depth = ctx.Run.Depth + 1,
            MaxConcurrency = 1
        }, ctx.Ct).ConfigureAwait(false);

        var final = child.Services.State.Items.GetValueOrDefault(forwarded.Id);
        var success = final?.State is WorkItemState.Done or WorkItemState.Verified;

        s.Record(new DelegationCompleted(ctx.Item.Id, childName, success, report.CostUsd));

        // Child spend rolls up to the parent's budget so a composite cannot exceed its ceiling
        // by hiding cost inside its children.
        if (report.CostUsd > 0) s.Budget.Record(ctx.Item, report.CostUsd);

        var detail = $"{childName}: {report.Summary}";

        var result = success
            ? StationResult.Ok(detail)
            : StationResult.GateFailed($"{detail}; child item ended {final?.State.ToString() ?? "missing"}" +
                                       (final?.LastError is { } e ? $" — {e}" : ""));

        return result with
        {
            DelegatedCostUsd = report.CostUsd,
            DelegatedUsage = report.Usage,
            DelegatedCalls = report.ModelCalls
        };
    }
}
