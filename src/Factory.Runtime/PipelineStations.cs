using System.Text;
using Factory.Core;

namespace Factory.Runtime;

internal static class PromptText
{
    /// <summary>Renders the item into the compact form stations receive. Kept terse on
    /// purpose — this text is paid for on every station call for every item.</summary>
    public static string Item(WorkItem item, bool withCriteria = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Title: {item.Title}");
        if (!string.IsNullOrWhiteSpace(item.Intent)) sb.AppendLine($"Intent: {item.Intent}");
        sb.AppendLine($"Kind: {item.Kind}");

        if (item.Requirements.Count > 0)
        {
            sb.AppendLine("Requirements:");
            foreach (var r in item.Requirements) sb.AppendLine($"- {r}");
        }

        if (withCriteria && item.AcceptanceCriteria.Count > 0)
        {
            sb.AppendLine("Acceptance criteria:");
            foreach (var c in item.AcceptanceCriteria)
                sb.AppendLine($"- {c.Statement} [{c.Verification.Describe}]");
        }

        return sb.ToString();
    }

    public static string Cap(string s, int max) =>
        s.Length <= max ? s : s[..max] + $"\n… (truncated at {max} chars)";
}

public sealed class DecomposeStation : AgentStation
{
    public override StationRole Role => StationRole.Decompose;

    public override async Task<StationResult> ExecuteAsync(StationContext ctx)
    {
        var s = ctx.Services;

        // An item that already has children was decomposed on an earlier pass.
        if (s.State.Children(ctx.Item.Id).Any())
            return StationResult.Ok("already decomposed", run: null);

        var prompt = s.Prompts.Select(ctx.Def.Id, s.Rng);
        var digest = RepoDigest.Build(s.Workspace.RepoRoot, Math.Min(ctx.Def.ContextByteCap, 4000));

        var user = $"""
            {PromptText.Item(ctx.Item)}

            Repository digest:
            {digest}
            """;

        var (run, record) = await InvokeAsync(ctx, prompt, user, Schemas.Decompose).ConfigureAwait(false);

        if (!run.Success)
            return StationResult.Failed(run.Error ?? "decompose failed", record with { GatePassed = false });

        if (!run.TryStructured<DecomposeResult>(out var decomposed, out var parseError))
            return StationResult.Failed($"unparseable decomposition: {parseError}", record with { GatePassed = false });

        // Nothing to split: the item is already a single unit of work, so it continues
        // down the pipeline itself rather than being wrapped in a pointless parent.
        if (decomposed.Children.Count <= 1)
        {
            var enriched = decomposed.Children.Count == 1 && ctx.Item.AcceptanceCriteria.Count == 0
                ? ctx.Item with
                {
                    AcceptanceCriteria = decomposed.Children[0].AcceptanceCriteria.Select(c => c.ToDomain()).ToList(),
                    Requirements = ctx.Item.Requirements.Count > 0
                        ? ctx.Item.Requirements
                        : decomposed.Children[0].Requirements
                }
                : ctx.Item;

            return StationResult.Ok("single unit; not decomposed", enriched, record with { GatePassed = true });
        }

        // Map child keys onto real ids so declared dependencies survive.
        var byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var built = new List<WorkItem>();

        foreach (var dto in decomposed.Children)
        {
            var child = dto.ToDomain(ctx.Item.Id, ctx.Item.Provenance) with
            {
                State = WorkItemState.Ready,
                Priority = ctx.Item.Priority,
                BudgetUsd = ctx.Item.BudgetUsd
            };
            if (!string.IsNullOrWhiteSpace(dto.Key)) byKey[dto.Key] = child.Id;
            built.Add(child);
        }

        var children = built.Select((child, i) =>
        {
            var deps = decomposed.Children[i].DependsOn
                .Select(k => byKey.GetValueOrDefault(k))
                .Where(id => id is not null && id != child.Id)
                .Select(id => id!)
                .Distinct()
                .ToList();
            return child with { DependsOn = deps };
        }).ToList();

        ctx.Log($"decomposed into {children.Count} items");

        return new StationResult
        {
            Success = true,
            GatePassed = true,
            Detail = $"decomposed into {children.Count} child items",
            NewItems = children,
            Run = record with { GatePassed = true },
            // The parent is a container: its children carry the work from here.
            ShortCircuitToDone = true
        };
    }
}

public sealed class PlanStation : AgentStation
{
    public override StationRole Role => StationRole.Plan;

    public override async Task<StationResult> ExecuteAsync(StationContext ctx)
    {
        var s = ctx.Services;
        var prompt = s.Prompts.Select(ctx.Def.Id, s.Rng);
        var digest = RepoDigest.Build(ctx.Run.WorkDir, ctx.Def.ContextByteCap);

        var user = $"""
            {PromptText.Item(ctx.Item)}

            Repository digest:
            {digest}
            """;

        var (run, record) = await InvokeAsync(ctx, prompt, user, Schemas.Plan).ConfigureAwait(false);

        if (!run.Success)
            return StationResult.Failed(run.Error ?? "plan failed", record with { GatePassed = false });

        if (!run.TryStructured<PlanResult>(out var plan, out var parseError))
            return StationResult.Failed($"unparseable plan: {parseError}", record with { GatePassed = false });

        ctx.Run.Plan = plan;
        ctx.Log($"planned {plan.Files.Count} file(s), {plan.Steps.Count} step(s)");
        return StationResult.Ok($"{plan.Files.Count} files, {plan.Steps.Count} steps", run: record with { GatePassed = true });
    }
}

public sealed class ImplementStation : AgentStation
{
    public override StationRole Role => StationRole.Implement;

    public override async Task<StationResult> ExecuteAsync(StationContext ctx)
    {
        var s = ctx.Services;
        var prompt = s.Prompts.Select(ctx.Def.Id, s.Rng);

        // Thick profile deliberately keeps the default system preamble so the cache prefix
        // stays byte-identical across runs; the station's own instructions therefore travel
        // in the user message instead. Measured: replacing the preamble here costs ~2.5x more.
        var sb = new StringBuilder();
        sb.AppendLine(prompt.Text);
        sb.AppendLine();
        sb.AppendLine(PromptText.Item(ctx.Item));

        if (ctx.Run.Plan is { } plan && plan.Files.Count + plan.Steps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Plan:");
            sb.AppendLine(plan.ToPromptText());
        }

        if (ctx.Run.LastFailure is { Length: > 0 } failure)
        {
            sb.AppendLine();
            sb.AppendLine("The previous attempt was rejected. Fix exactly this:");
            sb.AppendLine(PromptText.Cap(failure, 3000));
        }

        var (run, record) = await InvokeAsync(ctx, prompt, sb.ToString(), noCache: true).ConfigureAwait(false);

        if (!run.Success)
            return StationResult.Failed(run.Error ?? "implementation failed", record with { GatePassed = false });

        if (!await s.Workspace.HasChangesAsync(ctx.Run.WorkDir, ctx.Ct).ConfigureAwait(false))
        {
            return StationResult.GateFailed(
                "no file changes were produced. " + PromptText.Cap(run.Text, 800),
                record with { GatePassed = false });
        }

        ctx.Log($"changed files in {run.Turns} turn(s), ${run.CostUsd:F4}");
        return StationResult.Ok("changes produced", run: record with { GatePassed = true });
    }
}

/// <summary>Deterministic gate. Costs nothing and cannot be argued with.</summary>
public sealed class VerifyStation : IStation
{
    public StationRole Role => StationRole.Verify;

    public async Task<StationResult> ExecuteAsync(StationContext ctx)
    {
        if (ctx.Item.AcceptanceCriteria.Count == 0)
            return StationResult.Ok("no acceptance criteria to check");

        var outcome = await DeterministicVerifier
            .VerifyAsync(ctx.Item, ctx.Run.WorkDir, ctx.Ct).ConfigureAwait(false);

        ctx.Run.DeferredCriteria = outcome.Deferred;

        var checkedCount = outcome.Report.Results.Count;
        ctx.Log($"{checkedCount} criteria checked at zero token cost" +
                (outcome.HasDeferred ? $", {outcome.Deferred.Count} deferred to review" : ""));

        if (!outcome.DeterministicPassed)
            return StationResult.GateFailed(outcome.Report.Summary);

        return StationResult.Ok(
            checkedCount == 0 ? "no deterministic criteria" : $"all {checkedCount} deterministic criteria passed");
    }
}

public sealed class ReviewStation : AgentStation
{
    public override StationRole Role => StationRole.Review;

    public override async Task<StationResult> ExecuteAsync(StationContext ctx)
    {
        var s = ctx.Services;

        // Early exit: when every criterion was machine-checked and passed, and nothing was
        // deferred, there is nothing a model can add that the commands did not already prove.
        if (ctx.Run.DeferredCriteria.Count == 0 && ctx.Item.IsFullyDeterministic)
        {
            ctx.Log("skipped — all criteria were machine-verified (no model call)");
            return StationResult.Ok("review skipped; fully machine-verified");
        }

        var prompt = s.Prompts.Select(ctx.Def.Id, s.Rng);
        var diff = await s.Workspace.DiffAsync(ctx.Run.WorkDir, ctx.Ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine(PromptText.Item(ctx.Item));

        if (ctx.Run.DeferredCriteria.Count > 0)
        {
            sb.AppendLine("Criteria requiring your judgement:");
            foreach (var c in ctx.Run.DeferredCriteria)
                sb.AppendLine($"- {c.Statement} [{c.Verification.Describe}]");
        }

        sb.AppendLine();
        sb.AppendLine("Diff:");
        sb.AppendLine(PromptText.Cap(diff, ctx.Def.ContextByteCap));

        var (run, record) = await InvokeAsync(ctx, prompt, sb.ToString(), Schemas.Review).ConfigureAwait(false);

        if (!run.Success)
            return StationResult.Failed(run.Error ?? "review failed", record with { GatePassed = false });

        if (!run.TryStructured<ReviewResult>(out var review, out var parseError))
            return StationResult.Failed($"unparseable review: {parseError}", record with { GatePassed = false });

        // Follow-ups become real work items rather than advice nobody acts on.
        var followUps = review.FollowUp
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => WorkItem.Create(f, $"Raised while reviewing {ctx.Item.Id}", WorkItemKind.Improvement) with
            {
                State = WorkItemState.Draft,
                Provenance = Provenance.FromAgent(ctx.Def.Id),
                ParentId = ctx.Item.ParentId,
                Priority = ctx.Item.Priority + 50
            })
            .ToList();

        if (!review.Pass)
        {
            var detail = review.Findings.Count > 0
                ? string.Join("; ", review.Findings)
                : review.Summary;
            return new StationResult
            {
                Success = true, GatePassed = false, Detail = detail,
                NewItems = followUps, Run = record with { GatePassed = false }
            };
        }

        return new StationResult
        {
            Success = true, GatePassed = true, Detail = review.Summary,
            NewItems = followUps, Run = record with { GatePassed = true }
        };
    }
}

/// <summary>Merges verified work into the mainline. Deterministic; the only station that
/// writes to the user's checkout.</summary>
public sealed class IntegrateStation : IStation
{
    public StationRole Role => StationRole.Integrate;

    public async Task<StationResult> ExecuteAsync(StationContext ctx)
    {
        var message = $"{ctx.Item.Title}\n\n{ctx.Item.Intent}".Trim();
        var (ok, detail) = await ctx.Services.Workspace
            .IntegrateAsync(ctx.Item, ctx.Run.WorkDir, message, ctx.Ct).ConfigureAwait(false);

        if (!ok) return StationResult.GateFailed(detail);

        ctx.Log(detail);
        return StationResult.Ok(detail);
    }
}
