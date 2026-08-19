using System.Collections.Concurrent;
using Factory.Agents;
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

    /// <summary>How often an in-flight item's claim is refreshed. Defaults to a third of the
    /// shortest lease the factory has to survive, so two refreshes can be missed before a claim
    /// is lost. Only lowered by tests.</summary>
    public TimeSpan LeaseRefreshInterval { get; init; } = Leases.RefreshInterval;
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
public sealed class Orchestrator : IDisposable
{
    private readonly FactoryHost host;
    private readonly FactoryServices _s;
    private readonly HeartbeatWriter _heartbeat;
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;

    private int _completed, _failed, _blocked, _delegatedCalls;
    private decimal _delegatedCost;
    private TokenUsage _delegatedUsage = TokenUsage.Zero;
    private readonly Lock _tally = new();
    private int _disposed;

    /// <summary>How long an item may sit in the same station before it is considered stalled.
    /// Not yet consulted anywhere — reserved for a later change.</summary>
    public TimeSpan StallThreshold { get; }

    /// <summary>Last time each item made forward progress — a station change or a shell command
    /// starting/completing on its behalf. Not yet consulted anywhere; the foundation for a later
    /// stall check against <see cref="StallThreshold"/>.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastProgressUtc = new();

    /// <summary>The claims this run took itself and is still working, which is exactly the set whose
    /// leases it has to keep alive. Written from the claim rather than read back out of the fold: the
    /// mirror's ledger append is best-effort, so a swallowed one leaves a bead claimed under a live
    /// lease that the fold has never heard of — and a shared backlog puts other machines' in-flight
    /// items in the fold, whose leases are not this checkout's to refresh.</summary>
    private readonly ConcurrentDictionary<string, bool> _claimsHeld = new();

    public Orchestrator(FactoryHost host, TimeSpan? stallThreshold = null)
    {
        this.host = host;
        _s = host.Services;
        _heartbeat = new HeartbeatWriter(host.Paths);
        StallThreshold = stallThreshold ?? TimeSpan.FromSeconds(120);

        // Best-effort net so a killed or crashing process still leaves the heartbeat file
        // saying 'stopped' rather than stuck on the last 'running' write.
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        // Shell commands run for an item deep inside a station (check, verify, remediation);
        // this is how their start/completion reaches progress tracking without threading an
        // item id through every call site.
        Shell.OnCommandStarted = TouchProgress;
        Shell.OnCommandCompleted = TouchProgress;
    }

    /// <summary>Records that an item just made forward progress.</summary>
    private void TouchProgress(string itemId) => _lastProgressUtc[itemId] = DateTimeOffset.UtcNow;

    public async Task<OrchestratorReport> RunAsync(
        OrchestratorOptions? options = null, CancellationToken ct = default)
    {
        var opts = options ?? new OrchestratorOptions();
        var configured = Math.Max(1, opts.MaxConcurrency ?? _s.Config.MaxConcurrency);

        RequeueOrphans();

        // Baseline the mainline before anything else compiles, so the deterministic gate is
        // measured against a quiet machine rather than one already under load.
        if (_s.Blueprint.Stations.Any(s => s.Role == StationRole.Check))
        {
            try
            {
                await CheckStation.CaptureBaselineAsync(
                    _s, ct, repoStateProvider: new GitRepoStateProvider(_s.Workspace.RepoRoot)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _s.Log($"  [check] could not baseline the mainline: {ex.Message}");
            }
        }

        var started = 0;
        var running = new List<Task>();

        // A claim carries a lease that expires while the station is still working, so refreshing
        // has to span the whole run. It cannot live in the loop below: the loop exits as soon as
        // MaxItems have been started, and the items it started then run to completion in the
        // drain after it — with MaxItems of 1, that is the entire run.
        using var leases = new HeartbeatTimer(RefreshClaimsAsync, opts.LeaseRefreshInterval);
        leases.Start();

        while (!ct.IsCancellationRequested && started < opts.MaxItems)
        {
            running.RemoveAll(t => t.IsCompleted);

            // Concurrency is re-derived every pass: when the provider reports we are close to
            // a usage ceiling the factory narrows itself rather than sprinting into the wall.
            var concurrency = _s.Runner.Governor.Concurrency(configured);

            // A rejected window means nothing new should be started at all. In-flight items
            // are left to drain, so no verified work is lost to a limit we just hit.
            var throttled = _s.Runner.Governor.ShouldHold(out var holdFor, out var holdReason) &&
                            _s.Runner.Governor.Binding?.Status == RateLimitStatus.Rejected;

            if (throttled && running.Count == 0)
            {
                if (holdFor > _s.Runner.Governor.Policy.MaxWait || opts.StopWhenIdle && holdFor > opts.PollInterval * 4)
                {
                    _s.Log($"⏸ {holdReason} — stopping; remaining work stays queued");
                    break;
                }

                _s.Log($"⏸ {holdReason}");
                try { await Task.Delay(holdFor, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            var claimable = throttled ? 0 : concurrency - running.Count;

            // Claiming marks the item in progress before it is dispatched, so the next poll
            // cannot pick it up again.
            for (var i = 0; i < claimable && started < opts.MaxItems; i++)
            {
                if (_s.Items.TryClaim(_s.Config.Name) is not { } claimed) break;

                // Before anything else can fail: from here on there is a lease out in the backlog
                // with this checkout's name on it, and the refresh loop is the only thing keeping it.
                _claimsHeld[claimed.Id] = true;

                claimed = _s.Items.Update(claimed with
                {
                    Station = claimed.Station ?? _s.Blueprint.Pipeline.FirstOrDefault()
                });
                TouchProgress(claimed.Id);
                started++;
                running.Add(ProcessItemAsync(claimed, opts, ct));
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

        leases.Stop();

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

    /// <summary>Holds this run's own claims open while their stations work.</summary>
    private Task RefreshClaimsAsync()
    {
        RefreshEachClaim(_s.Items, _claimsHeld.Keys, _s.Log);

        return Task.CompletedTask;
    }

    /// <summary>Refreshes each claim independently, so one item's failure never costs the others
    /// theirs. Without the per-id guard a single throwing <c>Heartbeat</c> abandons the rest of the
    /// tick — at concurrency 2, one sick item costs its neighbour every refresh, and the neighbour's
    /// lease then expires while its station is still working. The timer swallows what escapes a tick,
    /// so there would be nothing to see it by either.
    ///
    /// <see cref="WorkItemStoreException"/> is the one caught because the store is always composed
    /// behind <see cref="GuardedWorkItemStore"/>, which is what every port fault arrives as — the same
    /// reasoning and the same shape as <see cref="RequeueOrphans"/>' per-orphan tolerance. Anything
    /// else is a defect in the factory and stays loud.
    ///
    /// Public for the reason <c>Commands.IsHeldElsewhere</c> is: the policy is not reachable through
    /// <see cref="RunAsync"/> without a test-only injection seam in the host's composition, and the
    /// seam would be the larger change.</summary>
    public static void RefreshEachClaim(IWorkItemStore items, IEnumerable<string> ids, Action<string> log)
    {
        foreach (var id in ids)
        {
            try
            {
                items.Heartbeat(id);
            }
            catch (WorkItemStoreException ex)
            {
                log($"the claim on {id} could not be refreshed, so its lease may expire before the " +
                    $"station finishes: {ex.Message}");
            }
        }
    }

    /// <summary>Work left mid-flight by a crash is put back on the queue. This is what the
    /// event-sourced ledger buys: a killed factory resumes rather than losing the item.
    ///
    /// Released rather than merely transitioned: releasing is what also drops the claim and clears
    /// the assignee, and an orphan that kept either could not be picked up by another machine.
    ///
    /// Only this checkout's own claims: a shared backlog puts every machine's in-flight work in the
    /// fold, and a backlog store is entitled to refuse a release of a claim it does not hold — which
    /// would take the whole run start down with it. Another checkout's item is reported and left
    /// alone, never forced: its holder may be working on it right now, and only its own restart or
    /// an expired lease can safely return it.
    ///
    /// One orphan that cannot be requeued is reported and stepped over rather than allowed to end the
    /// pass. The targets come from the fold while the backlog decides whether a release is legal, so
    /// the two can disagree — a bead another machine closed between this host's open and now is
    /// released as a Done item, which the port is right to refuse — and a single refusal must not stop
    /// a factory before it has started or cost every other orphan its requeue. The log line is the
    /// report: nothing is retried and nothing is forced.</summary>
    private void RequeueOrphans()
    {
        foreach (var item in _s.State.InFlight().ToList())
        {
            if (HeldElsewhere(item))
            {
                // "still holds it" is asserted, by
                // Requeueing_orphans_leaves_an_item_another_checkout_holds_in_progress_alone. It has no
                // other way to tell this report apart from the refusal logged below: without the guard
                // the release is attempted, bd refuses it, nothing is written, and bd's own refusal text
                // names the holder too — so the wording is what distinguishes them. Reword both together.
                _s.Log($"left {item.Id} ({item.Title}) in flight — {item.Owner} still holds it");
                continue;
            }

            try
            {
                _s.Items.Release(item.Id, "requeued after restart");
            }
            catch (WorkItemStoreException ex)
            {
                _s.Log($"could not requeue {item.Id} ({item.Title}) — the backlog refused it: {ex.Message}");
                continue;
            }

            _s.Log($"requeued {item.Id} ({item.Title})");
        }
    }

    // An item with no recorded owner is this checkout's: a backlog store that keeps no claims
    // reports none, and it is per-checkout anyway.
    private bool HeldElsewhere(WorkItem item) =>
        !string.IsNullOrEmpty(item.Owner) && item.Owner != _s.Config.Name;

    private async Task ProcessItemAsync(WorkItem item, OrchestratorOptions opts, CancellationToken ct)
    {
        var run = new ItemRun(item, _s.Workspace.RepoRoot, opts.Depth);
        var stationAttempts = new Dictionary<string, int>();
        var executions = 0;

        _s.Log($"▶ {item.Id} {item.Title}");

        // Attributes any shell command run on this item's behalf (check, verify, remediation)
        // to it, however deep in the call stack it happens.
        Shell.CurrentItemId.Value = item.Id;
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
                TouchProgress(run.Item.Id);

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

                if (result.SupersededByChildren)
                {
                    Supersede(run, result.Detail);
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
        finally
        {
            // However this pass ended, the item is no longer being worked here, so its lease is no
            // longer this run's to hold open.
            _claimsHeld.TryRemove(item.Id, out _);
            Shell.CurrentItemId.Value = null;
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

    /// <summary>Retires a parent that decomposition replaced with children. It skips the gates by
    /// design, so it is not counted as completed work and is never marked Done.</summary>
    private void Supersede(ItemRun run, string detail)
    {
        var item = host.Transition(run.Item, WorkItemState.Superseded, detail);
        host.Update(item with { Station = null });
        TouchProgress(item.Id);

        _s.Log($"↳ {item.Id} superseded — {Trim(detail)}");
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
        TouchProgress(item.Id);

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

    private void OnProcessExit(object? sender, EventArgs e) => TryWriteStoppedHeartbeat();

    /// <summary>Overwrites the heartbeat file with a 'stopped' status. Safe to call more than
    /// once — the write is a full overwrite, so a repeat is a no-op in effect — and never
    /// throws, since callers include Dispose and a process-exit handler.</summary>
    private void TryWriteStoppedHeartbeat()
    {
        try
        {
            _heartbeat.WriteAsync(new HeartbeatStatus
            {
                Pid = Environment.ProcessId,
                StartedAtUtc = _startedAtUtc,
                Status = "stopped",
                StoppedAtUtc = DateTime.UtcNow
            }).GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort: a failed heartbeat write must not crash Dispose or process teardown.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        Shell.OnCommandStarted = null;
        Shell.OnCommandCompleted = null;
        TryWriteStoppedHeartbeat();
    }
}
