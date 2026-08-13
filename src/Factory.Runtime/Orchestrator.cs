using Factory.Core;

namespace Factory.Runtime;

public sealed record OrchestratorOptions
{
    public int? MaxConcurrency { get; init; }

    /// <summary>Exit when the backlog is empty instead of waiting for new work.</summary>
    public bool StopWhenIdle { get; init; } = true;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Hard ceiling on station executions for one item. Retries route backwards
    /// through the pipeline, so this is the loop guard.</summary>
    public int MaxStationExecutionsPerItem { get; init; } = 30;

    public int MaxItems { get; init; } = int.MaxValue;

    public int Depth { get; init; }
}

public sealed record OrchestratorReport
{
    public int Completed { get; init; }
    public int Failed { get; init; }
    public int Blocked { get; init; }
    public decimal CostUsd { get; init; }
    public TokenUsage Usage { get; init; } = TokenUsage.Zero;
    public int ModelCalls { get; init; }
    public int CacheHits { get; init; }

    /// <summary>Portion of <see cref="CostUsd"/> spent inside linked child factories.</summary>
    public decimal DelegatedCostUsd { get; init; }

    public string Summary =>
        $"{Completed} completed, {Failed} failed, {Blocked} blocked · " +
        $"${CostUsd:F4}" +
        (DelegatedCostUsd > 0 ? $" (${DelegatedCostUsd:F4} in linked factories)" : "") +
        $" · {Usage.Total:N0} tokens · {ModelCalls} model calls, {CacheHits} cache hits";
}

/// <summary>
/// The autonomous loop. Pulls dispatchable work, runs it through the blueprint's pipeline
/// with bounded concurrency, enforces budget before every model call, and routes gate
/// failures back to the station that can fix them. Idles cheaply when there is nothing to do.
/// </summary>
public sealed class Orchestrator(FactoryHost host)
{
    private readonly FactoryServices _s = host.Services;

    private int _completed, _failed, _blocked, _delegatedCalls;
    private decimal _delegatedCost;
    private TokenUsage _delegatedUsage = TokenUsage.Zero;
    private readonly Lock _tally = new();

    public async Task<OrchestratorReport> RunAsync(
        OrchestratorOptions? options = null, CancellationToken ct = default)
    {
        var opts = options ?? new OrchestratorOptions();
        var concurrency = Math.Max(1, opts.MaxConcurrency ?? _s.Config.MaxConcurrency);

        RequeueOrphans();

        var started = 0;
        var running = new List<Task>();

        while (!ct.IsCancellationRequested && started < opts.MaxItems)
        {
            running.RemoveAll(t => t.IsCompleted);

            var claimable = concurrency - running.Count;
            if (claimable > 0)
            {
                var batch = _s.State.Dispatchable().Take(claimable).ToList();

                foreach (var item in batch)
                {
                    if (started >= opts.MaxItems) break;

                    // Claim before dispatching so the next poll cannot pick it up again.
                    var claimed = host.Transition(item, WorkItemState.InProgress, "dispatched");
                    claimed = host.Update(claimed with { Station = claimed.Station ?? _s.Blueprint.Pipeline.FirstOrDefault() });
                    started++;
                    running.Add(ProcessItemAsync(claimed, opts, ct));
                }

                if (batch.Count > 0) continue;
            }

            if (running.Count > 0)
            {
                await Task.WhenAny([.. running, Task.Delay(opts.PollInterval, ct)]).ConfigureAwait(false);
                continue;
            }

            if (opts.StopWhenIdle) break;

            try { await Task.Delay(opts.PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        try { await Task.WhenAll(running).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* drain */ }

        lock (_tally)
            return new OrchestratorReport
            {
                Completed = _completed,
                Failed = _failed,
                Blocked = _blocked,
                // Totals include work done inside child factories, which spend through their
                // own runners and would otherwise be invisible here.
                CostUsd = _s.Runner.TotalCostUsd + _delegatedCost,
                Usage = _s.Runner.TotalUsage + _delegatedUsage,
                ModelCalls = _s.Runner.Calls + _delegatedCalls,
                CacheHits = _s.Runner.CacheHits,
                DelegatedCostUsd = _delegatedCost
            };
    }

    /// <summary>Work left mid-flight by a crash is put back on the queue. This is what the
    /// event-sourced ledger buys: a killed factory resumes rather than losing the item.</summary>
    private void RequeueOrphans()
    {
        foreach (var item in _s.State.InFlight().ToList())
        {
            var requeued = host.Transition(item, WorkItemState.Ready, "requeued after restart");
            host.Update(requeued);
            _s.Log($"requeued {item.Id} ({item.Title})");
        }
    }

    private async Task ProcessItemAsync(WorkItem item, OrchestratorOptions opts, CancellationToken ct)
    {
        var run = new ItemRun(item, _s.Workspace.RepoRoot, opts.Depth);
        var stationAttempts = new Dictionary<string, int>();
        var executions = 0;

        _s.Log($"▶ {item.Id} {item.Title}");

        try
        {
            var stationId = item.Station ?? _s.Blueprint.Pipeline.FirstOrDefault();

            while (stationId is not null)
            {
                ct.ThrowIfCancellationRequested();

                if (++executions > opts.MaxStationExecutionsPerItem)
                {
                    await FailAsync(run, $"exceeded {opts.MaxStationExecutionsPerItem} station executions", ct)
                        .ConfigureAwait(false);
                    return;
                }

                var def = _s.Blueprint.Require(stationId);
                run.Item = host.Update(run.Item with { Station = stationId });

                if (def.Profile != TokenProfile.None)
                {
                    try
                    {
                        _s.Budget.EnsureCanSpend(run.Item);
                    }
                    catch (BudgetExhaustedException ex)
                    {
                        await BlockAsync(run, ex.Message, ct).ConfigureAwait(false);
                        return;
                    }
                }

                if (FactoryHost.NeedsWorkspace(def.Role) && run.WorkDir == _s.Workspace.RepoRoot)
                    run.WorkDir = await _s.Workspace.AcquireAsync(run.Item, ct).ConfigureAwait(false);

                var ctx = new StationContext { Services = _s, Def = def, Run = run, Ct = ct };
                var result = await ExecuteStationAsync(ctx).ConfigureAwait(false);

                if (result.DelegatedCostUsd > 0 || result.DelegatedCalls > 0)
                {
                    lock (_tally)
                    {
                        _delegatedCost += result.DelegatedCostUsd;
                        _delegatedUsage += result.DelegatedUsage;
                        _delegatedCalls += result.DelegatedCalls;
                    }
                }

                if (result.Run is { } record) _s.Record(new RunCompleted(record));
                _s.Record(new GateEvaluated(run.Item.Id, def.Id, result.GatePassed, result.Detail));

                foreach (var newItem in result.NewItems) host.Submit(newItem, activate: false);
                if (result.NewItems.Count > 0)
                    _s.Log($"  [{def.Id}] filed {result.NewItems.Count} new work item(s)");

                if (result.Item is { } updated) run.Item = host.Update(updated);

                if (result.ShortCircuitToDone)
                {
                    await CompleteAsync(run, result.Detail, ct).ConfigureAwait(false);
                    return;
                }

                if (result.Success && result.GatePassed)
                {
                    run.LastFailure = null;
                    stationId = _s.Blueprint.NextAfter(stationId);
                    continue;
                }

                // Gate failed or the station errored: route back if the blueprint says where.
                var attempts = stationAttempts[def.Id] = stationAttempts.GetValueOrDefault(def.Id) + 1;
                run.LastFailure = result.Detail;
                run.Item = host.Update(run.Item with { Attempts = run.Item.Attempts + 1, LastError = result.Detail });

                _s.Log($"  [{def.Id}] gate failed: {Trim(result.Detail)}");

                if (def.OnFail is { } fallback && attempts <= def.Retries)
                {
                    stationId = fallback;
                    continue;
                }

                if (def.EscalateToHuman)
                {
                    await BlockAsync(run, $"{def.Id}: {result.Detail}", ct).ConfigureAwait(false);
                    return;
                }

                await FailAsync(run, $"{def.Id}: {result.Detail}", ct).ConfigureAwait(false);
                return;
            }

            await CompleteAsync(run, "pipeline complete", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var requeued = host.Transition(run.Item, WorkItemState.Ready, "cancelled");
            host.Update(requeued);
        }
        catch (Exception ex)
        {
            await FailAsync(run, $"unhandled: {ex.Message}", ct).ConfigureAwait(false);
        }
    }

    private async Task<StationResult> ExecuteStationAsync(StationContext ctx)
    {
        try
        {
            return await host.Resolve(ctx.Def).ExecuteAsync(ctx).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return StationResult.Failed($"station threw: {ex.Message}");
        }
    }

    private async Task CompleteAsync(ItemRun run, string detail, CancellationToken ct)
    {
        var item = run.Item;
        if (item.State is WorkItemState.InProgress)
            item = host.Transition(item, WorkItemState.InReview, detail);
        if (item.State is WorkItemState.InReview)
            item = host.Transition(item, WorkItemState.Verified, detail);
        item = host.Transition(item, WorkItemState.Done, detail);
        host.Update(item with { Station = null });

        Interlocked.Increment(ref _completed);
        _s.Log($"✔ {item.Id} done — {Trim(detail)}");
        await Task.CompletedTask;
    }

    private async Task FailAsync(ItemRun run, string reason, CancellationToken ct)
    {
        var item = host.Transition(run.Item, WorkItemState.Failed, reason);
        host.Update(item with { LastError = reason });
        await DiscardAsync(run, ct).ConfigureAwait(false);

        Interlocked.Increment(ref _failed);
        _s.Log($"✘ {item.Id} failed — {Trim(reason)}");
    }

    private async Task BlockAsync(ItemRun run, string reason, CancellationToken ct)
    {
        var item = host.Transition(run.Item, WorkItemState.Blocked, reason);
        host.Update(item with { LastError = reason });

        Interlocked.Increment(ref _blocked);
        _s.Log($"⏸ {item.Id} blocked — {Trim(reason)}");
        await Task.CompletedTask;
    }

    private async Task DiscardAsync(ItemRun run, CancellationToken ct)
    {
        try
        {
            await _s.Workspace.DiscardAsync(run.Item, run.WorkDir, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _s.Log($"  could not discard workspace for {run.Item.Id}: {ex.Message}");
        }
    }

    private static string Trim(string s)
    {
        s = s.Replace('\n', ' ').Trim();
        return s.Length <= 160 ? s : s[..160] + "…";
    }
}
