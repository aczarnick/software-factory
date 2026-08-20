using Factory.Agents;
using Factory.Core;
using Factory.Evolution;
using Factory.Runtime;

namespace Factory.Cli;

public static class Commands
{
    private static string ResolveDir(CommandLine cli) =>
        Path.GetFullPath(cli.Get("dir") ?? Directory.GetCurrentDirectory());

    private static BudgetSpec? BudgetOverride(CommandLine cli)
    {
        var daily = cli.Amount("budget");
        var perItem = cli.Amount("item-budget");
        if (daily is null && perItem is null) return null;

        var baseline = new BudgetSpec();
        return baseline with
        {
            DailyUsd = daily ?? baseline.DailyUsd,
            PerItemUsd = perItem ?? Math.Min(baseline.PerItemUsd, daily ?? baseline.PerItemUsd)
        };
    }

    /// <summary>Opens the factory here, deploying one first if there is none. This is what
    /// makes `factory up` and `factory build` single-command operations.</summary>
    private static FactoryHost OpenOrInit(CommandLine cli, bool quiet = false)
    {
        var dir = ResolveDir(cli);
        var paths = FactoryPaths.Discover(dir) ?? new FactoryPaths(dir);

        if (!paths.Exists)
        {
            if (!quiet) Output.Step($"no factory here — deploying one into {paths.RepoRoot}");
            using var seeded = FactoryHost.Init(paths.RepoRoot, BlueprintFor(cli));
            if (!quiet) Output.Success($"deployed factory '{seeded.Config.Name}'");
        }

        return FactoryHost.Open(paths.RepoRoot, Output.Info, BudgetOverride(cli));
    }

    private static Blueprint? BlueprintFor(CommandLine cli) =>
        cli.Get("blueprint") switch
        {
            null or "standard" => Blueprint.Standard(),
            var other => throw new InvalidOperationException(
                $"Unknown blueprint '{other}'. Available: standard. " +
                "Composites are created by linking children with `factory link --pipeline`.")
        };

    // ── init ────────────────────────────────────────────────────────────────

    public static int Init(CommandLine cli)
    {
        var dir = ResolveDir(cli);
        using var host = FactoryHost.Init(dir, BlueprintFor(cli), log: Output.Info);

        Output.Success($"deployed factory '{host.Config.Name}' at {host.Paths.Root}");
        Output.Step($"blueprint: {host.Blueprint.Name} · pipeline: {string.Join(" → ", host.Blueprint.Pipeline)}");
        Output.Step($"budget: ${host.Blueprint.Budget.DailyUsd}/day, ${host.Blueprint.Budget.PerItemUsd}/item");
        Output.Line();
        Output.Info($"Next: {Output.Bold("factory build \"<what you want>\"")}  or  {Output.Bold("factory up")}");
        return 0;
    }

    // ── up ──────────────────────────────────────────────────────────────────

    public static async Task<int> Up(CommandLine cli, CancellationToken ct)
    {
        using var host = OpenOrInit(cli);
        var daemon = cli.Has("daemon") || cli.Has("watch");

        // Said before any work starts: a factory improving its own source will otherwise build with
        // code it is not running, and every command it adds to itself stays missing until reinstall.
        if (await HarnessStaleness.ProbeAsync(host.Paths.RepoRoot, ct).ConfigureAwait(false)
            is { IsStale: true } stale)
            Output.Warn(stale.Describe);

        if (cli.Has("include-proposed"))
        {
            var activated = 0;
            foreach (var item in host.Services.State.Items.Values.Where(i => i.State == WorkItemState.Draft))
            {
                host.Activate(item);
                activated++;
            }
            if (activated > 0) Output.Step($"activated {activated} proposed item(s)");
        }

        var pending = host.Services.State.Dispatchable().Count;
        if (pending == 0 && !daemon)
        {
            Output.Info("no work to do.");
            Output.Step("file some with `factory build \"<prompt>\"` or `factory add \"<title>\"`.");
            return 0;
        }

        Output.Header($"factory '{host.Config.Name}' — {pending} item(s) ready");

        var report = await host.CreateOrchestrator().RunAsync(new OrchestratorOptions
        {
            StopWhenIdle = !daemon,
            MaxConcurrency = cli.Number("concurrency"),
            MaxItems = cli.Number("max-items") ?? int.MaxValue,
            PollInterval = TimeSpan.FromSeconds(cli.Number("poll") ?? host.Config.PollSeconds)
        }, ct);

        Output.Header("Result");
        Output.Info(report.Summary);

        await MaybeEvolveAsync(host, cli, ct);
        return report.Failed > 0 ? 2 : 0;
    }

    // ── build ───────────────────────────────────────────────────────────────

    public static async Task<int> Build(CommandLine cli, CancellationToken ct)
    {
        var request = cli.PositionalText;
        if (string.IsNullOrWhiteSpace(request))
        {
            Output.Error("give me something to build: factory build \"a CLI todo app in Python\"");
            return 1;
        }

        using var host = OpenOrInit(cli);
        Output.Header($"Intake — {Output.Truncate(request, 70)}");

        var items = await ElicitAsync(host, request, cli, ct);
        if (items is null) return 1;

        foreach (var item in items) host.Submit(item);

        Output.Success($"filed {items.Count} work item(s)");
        foreach (var item in items)
            Output.Step($"  {item.Id}  {Output.Truncate(item.Title, 64)}  " +
                        $"({item.AcceptanceCriteria.Count} criteria, " +
                        $"{item.AcceptanceCriteria.Count(c => c.Verification.IsDeterministic)} machine-checked)");

        Output.Header("Building");

        var report = await host.CreateOrchestrator().RunAsync(new OrchestratorOptions
        {
            StopWhenIdle = true,
            MaxConcurrency = cli.Number("concurrency"),
            PollInterval = TimeSpan.FromSeconds(2)
        }, ct);

        Output.Header("Result");
        Output.Info(report.Summary);

        var state = host.Services.State;
        var failures = state.Items.Values
            .Where(i => i.State is WorkItemState.Failed or WorkItemState.Blocked)
            .ToList();

        foreach (var f in failures)
            Output.Warn($"{f.Id} {Output.Truncate(f.Title, 50)} — {Output.Truncate(f.LastError ?? "", 90)}");

        if (report.Completed > 0)
            Output.Success($"built in {host.Paths.RepoRoot}");

        await MaybeEvolveAsync(host, cli, ct);
        return failures.Count > 0 ? 2 : 0;
    }

    // ── intake ──────────────────────────────────────────────────────────────

    public static async Task<int> Intake(CommandLine cli, CancellationToken ct)
    {
        using var host = OpenOrInit(cli);

        var request = cli.PositionalText;
        if (string.IsNullOrWhiteSpace(request))
        {
            Console.Write(Output.Bold("What do you want built? "));
            request = Console.ReadLine() ?? "";
            if (string.IsNullOrWhiteSpace(request)) return 1;
        }

        var items = await ElicitAsync(host, request, cli, ct);
        if (items is null) return 1;

        foreach (var item in items)
        {
            host.Submit(item);
            Output.Success($"{item.Id}  {item.Title}");
            foreach (var c in item.AcceptanceCriteria)
                Output.Step($"    ✓ {c.Statement}  [{c.Verification.Describe}]");
            foreach (var a in item.Assumptions)
                Output.Step($"    assumed: {a}");
        }

        Output.Line();
        Output.Info($"Run {Output.Bold("factory up")} to build it.");
        return 0;
    }

    /// <summary>Shared intake conversation. Interactive by default; falls back to
    /// assumption-recording mode when there is no one to ask.</summary>
    private static async Task<IReadOnlyList<WorkItem>?> ElicitAsync(
        FactoryHost host, string request, CommandLine cli, CancellationToken ct)
    {
        var intake = host.CreateIntake();
        var interactive = !cli.Has("yes") && !Console.IsInputRedirected;
        var answers = new List<(string, string)>();

        for (var round = 0; round < 3; round++)
        {
            var outcome = await intake.ElicitAsync(request, answers, interactive, ct: ct);

            if (!outcome.Ok)
            {
                Output.Error(outcome.Error ?? "intake failed");
                return null;
            }

            if (!outcome.NeedsAnswers)
            {
                if (outcome.Items.Count == 0)
                {
                    Output.Error("intake produced no work items");
                    return null;
                }
                Output.Step($"intake cost ${outcome.CostUsd:F4}");
                return outcome.Items;
            }

            Output.Line();
            foreach (var question in outcome.Questions)
            {
                Console.Write(Output.Cyan("? ") + question + " ");
                var answer = Console.ReadLine() ?? "";
                answers.Add((question, string.IsNullOrWhiteSpace(answer) ? "(no preference — decide for me)" : answer));
            }
        }

        // Out of rounds: stop asking and let it commit to assumptions.
        var final = await intake.ElicitAsync(request, answers, allowQuestions: false, ct: ct);
        if (!final.Ok || final.Items.Count == 0)
        {
            Output.Error(final.Error ?? "intake produced no work items");
            return null;
        }
        return final.Items;
    }

    // ── add / activate ──────────────────────────────────────────────────────

    public static int Add(CommandLine cli)
    {
        var title = cli.PositionalText;
        if (string.IsNullOrWhiteSpace(title))
        {
            Output.Error("usage: factory add \"<title>\" [--criterion \"<shell command>\"] [--kind Feature]");
            return 1;
        }

        using var host = OpenOrInit(cli);

        var kind = Enum.TryParse<WorkItemKind>(cli.Get("kind"), ignoreCase: true, out var k) ? k : WorkItemKind.Feature;
        var item = WorkItem.Create(title, cli.Get("intent") ?? title, kind);

        if (cli.Get("criterion") is { Length: > 0 } command)
            item = item with { AcceptanceCriteria = [AcceptanceCriterion.Command(title, command)] };

        var filed = host.Submit(item);
        Output.Success($"{filed.Id}  {filed.Title}");
        if (item.AcceptanceCriteria.Count == 0)
            Output.Warn("no acceptance criteria — this item can only be verified by review. " +
                        "Add --criterion for a machine-checked gate.");
        return 0;
    }

    public static int Activate(CommandLine cli)
    {
        using var host = OpenOrInit(cli);
        var state = host.Services.State;

        var activatable = new[] { WorkItemState.Draft, WorkItemState.Blocked, WorkItemState.Failed };

        var targets = cli.Has("all")
            ? state.Items.Values.Where(i => activatable.Contains(i.State)).ToList()
            : state.Items.Values.Where(i => i.Id == cli.First).ToList();

        if (targets.Count == 0)
        {
            Output.Warn("nothing to activate");
            Output.Step("`factory activate --all` queues proposed, blocked, and failed items.");
            return 1;
        }

        foreach (var item in targets)
        {
            host.Activate(item);
            Output.Success($"{item.Id}  {Output.Truncate(item.Title, 70)}");
        }
        return 0;
    }

    public static int Cancel(CommandLine cli)
    {
        using var host = OpenOrInit(cli);
        var id = cli.First;
        if (id is null || !host.Services.State.Items.TryGetValue(id, out var item))
        {
            Output.Error($"no work item '{id}'");
            return 1;
        }

        var cancelled = host.Transition(item, WorkItemState.Cancelled, "cancelled");
        host.Update(cancelled);
        Output.Success($"{item.Id}  cancelled");
        return 0;
    }

    // ── inspection ──────────────────────────────────────────────────────────

    private static string Short(string? sha) =>
        string.IsNullOrEmpty(sha) ? "an unknown commit" : sha[..Math.Min(12, sha.Length)];

    /// <summary>Probes harness staleness from a synchronous command. Blocking is safe here: a CLI has
    /// no synchronisation context to deadlock against, and the probe is two short git invocations.</summary>
    private static HarnessStaleness ProbeHarness(FactoryHost host) =>
        HarnessStaleness.ProbeAsync(host.Paths.RepoRoot).GetAwaiter().GetResult();

    public static int Status(CommandLine cli)
    {
        using var host = OpenOrInit(cli, quiet: true);
        var state = host.Services.State;
        var items = state.Items.Values.ToList();

        Output.Header($"factory '{host.Config.Name}'");
        Output.Info($"version   {FactoryVersion.Full}");
        Output.Info($"root      {host.Paths.RepoRoot}");

        if (ProbeHarness(host) is { IsStale: true } stale)
            Output.Warn($"harness   {stale.Describe}");

        if (ToolchainRunner.TryLoadBaseline(host.Paths.BaselineFile) is { DisabledGates.Count: > 0 } baseline)
            Output.Warn($"gates off {string.Join(", ", baseline.DisabledGates)} "
                        + $"— recorded as already failing at {Short(baseline.Commit)}, so they cannot block");
        Output.Info($"blueprint {host.Blueprint.Name} · {string.Join(" → ", host.Blueprint.Pipeline)}");

        // The deterministic gates the repository itself provides, shown so it is obvious
        // what guarantees are in force beyond model-authored acceptance criteria.
        Output.Info($"toolchain {Toolchain.Detect(host.Paths.RepoRoot).Describe}");

        if (host.Blueprint.Factories.Count > 0)
            Output.Info($"linked    {string.Join(", ", host.Blueprint.Factories.Keys)}");

        foreach (var window in host.Services.Runner.Governor.Windows)
            Output.Info($"usage     {window.Describe(DateTimeOffset.UtcNow)}");

        Output.Header("Backlog");
        if (items.Count == 0)
        {
            Output.Step("empty");
        }
        else
        {
            var rows = items.GroupBy(i => i.State)
                .OrderBy(g => g.Key)
                .Select(g => new[] { Output.State(g.Key), g.Count().ToString() })
                .ToList();
            Output.Table(rows, "state", "count");
        }

        Output.Header("Spend");
        Output.Info($"total      ${state.TotalSpentUsd:F4}");
        Output.Info($"today      ${host.Services.Budget.DailySpent:F4} of ${host.Blueprint.Budget.DailyUsd:F2}");
        Output.Info($"tokens     {state.TotalUsage.Total:N0}");
        Output.Info($"model runs {state.Runs.Count}");
        return 0;
    }

    public static int List(CommandLine cli)
    {
        using var host = OpenOrInit(cli, quiet: true);
        var state = host.Services.State;
        var items = state.Items.Values
            .Where(i => cli.Has("all") || i.State is not (WorkItemState.Done or WorkItemState.Superseded))
            .OrderBy(i => i.State)
            .ThenBy(i => i.Priority)
            .ToList();

        if (items.Count == 0)
        {
            Output.Info("nothing here. `factory ls --all` includes completed work.");
            return 0;
        }

        var rows = items.Select(i => new[]
        {
            i.Id,
            Output.State(i.State),
            Output.Truncate(i.Title, 54),
            Verdict(state.VerdictFor(i.Id), i),
            $"${state.SpentFor(i.Id):F3}",
            i.Provenance.Kind.ToString().ToLowerInvariant()
        }).ToList();

        Output.Table(rows, "id", "state", "title", "passed", "cost", "from");
        return 0;
    }

    /// <summary>How many acceptance criteria actually passed. A dash means the item was never
    /// verified, which must not look like a pass: the column used to show how many criteria a shell
    /// *could* settle, so an item that skipped verification entirely still rendered as "5/5".
    ///
    /// Counted against the criteria a command can settle rather than all of them. Judged criteria are
    /// deferred to the review station and never appear in a deterministic verdict, so including them
    /// in the total would leave a fully verified item reading "3/5" for the rest of its life.</summary>
    private static string Verdict(VerificationReport? report, WorkItem item)
    {
        if (item.AcceptanceCriteria.Count == 0) return "—";

        var machine = item.AcceptanceCriteria.Count(c => c.Verification.IsDeterministic);
        if (machine == 0) return "judged";

        return report is null ? $"—/{machine}" : $"{report.Results.Count(r => r.Passed)}/{machine}";
    }

    public static int Show(CommandLine cli)
    {
        using var host = OpenOrInit(cli, quiet: true);
        var id = cli.First;
        if (id is null || !host.Services.State.Items.TryGetValue(id, out var item))
        {
            Output.Error($"no work item '{id}'");
            return 1;
        }

        Output.Header($"{item.Id}  {item.Title}");
        Output.Info($"state    {Output.State(item.State)}");
        Output.Info($"kind     {item.Kind}   priority {item.Priority}   from {item.Provenance}");
        Output.Info($"spend    ${host.Services.State.SpentFor(item.Id):F4}   attempts {item.Attempts}");
        if (item.Station is not null) Output.Info($"station  {item.Station}");
        if (!string.IsNullOrWhiteSpace(item.Intent)) Output.Info($"intent   {item.Intent}");

        if (item.Requirements.Count > 0)
        {
            Output.Header("Requirements");
            foreach (var r in item.Requirements) Output.Info($"- {r}");
        }

        if (item.AcceptanceCriteria.Count > 0)
        {
            var verdict = host.Services.State.VerdictFor(item.Id);

            Output.Header("Acceptance criteria");
            foreach (var c in item.AcceptanceCriteria)
            {
                var tag = c.Verification.IsDeterministic ? Output.Green("machine") : Output.Yellow("judged");
                Output.Info($"- [{tag}] {c.Statement}");
                Output.Step($"    {c.Verification.Describe}");

                // Absence of a result is reported as such. Saying nothing here is what let an item
                // that never reached verification read as though it had passed.
                var result = verdict?.Results.FirstOrDefault(r => r.CriterionId == c.Id);
                Output.Step(result switch
                {
                    { Passed: true } => $"    {Output.Green("passed")} — {result.Detail}",
                    { Passed: false } => $"    {Output.Red("failed")} — {result.Detail}",
                    // A judged criterion has no deterministic verdict by design; saying it was never
                    // checked would blame verification for declining to settle what it cannot.
                    _ when !c.Verification.IsDeterministic => $"    {Output.Dim("deferred to review")}",
                    _ => $"    {Output.Dim("never checked")}"
                });
            }
        }

        if (item.Assumptions.Count > 0)
        {
            Output.Header("Assumptions");
            foreach (var a in item.Assumptions) Output.Info($"- {a}");
        }

        var runs = host.Services.History.RunsForItem(item.Id);
        if (runs.Count > 0)
        {
            Output.Header("Runs");
            Output.Table(runs.Select(r => new[]
            {
                r.StationId,
                r.PromptVersion,
                r.GatePassed ? Output.Green("pass") : Output.Red("fail"),
                $"${r.CostUsd:F4}",
                $"{r.Usage.Total:N0}",
                r.CacheHit ? "cached" : $"{r.Turns} turns"
            }).ToList(), "station", "prompt", "gate", "cost", "tokens", "");
        }

        if (item.LastError is { Length: > 0 })
        {
            Output.Header("Last error");
            Output.Info(item.LastError);
        }
        return 0;
    }

    // ── link ────────────────────────────────────────────────────────────────

    public static int Link(CommandLine cli)
    {
        var childPath = cli.First;
        if (childPath is null)
        {
            Output.Error("usage: factory link <path-to-child> [--as <name>] [--pipeline]");
            return 1;
        }

        using var host = OpenOrInit(cli);

        var resolved = Path.GetFullPath(childPath);
        if (!Directory.Exists(resolved))
        {
            Output.Error($"no directory at {resolved}");
            return 1;
        }

        var name = cli.Get("as") ?? new DirectoryInfo(resolved).Name;

        var childPaths = new FactoryPaths(resolved);
        if (!childPaths.Exists)
        {
            Output.Step($"child has no factory — deploying one into {resolved}");
            using var seeded = FactoryHost.Init(resolved);
        }

        var config = host.Config;
        config.Factories[name] = resolved;
        File.WriteAllText(host.Paths.Config, FactoryJson.Write(config, pretty: true));

        var blueprint = host.Blueprint with { Factories = config.Factories };

        if (blueprint.Station(name) is null)
        {
            blueprint = blueprint with
            {
                Stations = [.. blueprint.Stations, new StationDef
                {
                    Id = name,
                    Role = StationRole.Delegate,
                    Tier = ModelTier.None,
                    Profile = TokenProfile.None,
                    DelegateTo = name,
                    Retries = 0,
                    EscalateToHuman = true
                }]
            };
        }

        if (cli.Has("pipeline") && !blueprint.Pipeline.Contains(name))
        {
            // A composite routes decomposed work to its children instead of building it
            // itself, so the pipeline collapses to decompose plus the delegates in order.
            var existingDelegates = blueprint.Pipeline.Where(p => IsDelegate(blueprint, p));
            blueprint = blueprint with { Pipeline = ["decompose", .. existingDelegates, name] };
        }

        var errors = blueprint.Validate().ToList();
        if (errors.Count > 0)
        {
            Output.Error("resulting blueprint is invalid: " + string.Join("; ", errors));
            return 1;
        }

        File.WriteAllText(host.Paths.BlueprintFile, FactoryJson.Write(blueprint, pretty: true));
        host.Services.Record(new FactoryLinked(name, resolved, "in"));

        Output.Success($"linked '{name}' → {resolved}");
        Output.Step(cli.Has("pipeline")
            ? $"pipeline is now: {string.Join(" → ", blueprint.Pipeline)}"
            : $"added delegate station '{name}'; add --pipeline to route work through it");
        return 0;
    }

    private static bool IsDelegate(Blueprint bp, string stationId) =>
        bp.Station(stationId)?.Role == StationRole.Delegate;

    // ── evolve / report ─────────────────────────────────────────────────────

    public static async Task<int> Evolve(CommandLine cli, CancellationToken ct)
    {
        using var host = OpenOrInit(cli);
        Output.Header($"Evolution — factory '{host.Config.Name}'");

        var settings = new GateSettings
        {
            MinSamples = cli.Number("min-samples") ?? GateSettings.Default.MinSamples,
            MinChampionSamples = cli.Number("min-champion-samples") ?? GateSettings.Default.MinChampionSamples
        };

        var report = await new EvolutionService(host).RunAsync(settings, ct);
        Output.Header("Result");
        Output.Info(report.Summary);
        return 0;
    }

    private static async Task MaybeEvolveAsync(FactoryHost host, CommandLine cli, CancellationToken ct)
    {
        if (cli.Has("no-evolve") || !host.Config.EvolutionEnabled) return;

        var runs = host.Services.History.Totals().RunCount;
        if (!cli.Has("evolve") && runs < host.Config.EvolveEveryRuns) return;

        Output.Header("Evolution");
        var report = await new EvolutionService(host).RunAsync(ct: ct);
        Output.Info(report.Summary);
    }

    public static int Report(CommandLine cli)
    {
        using var host = OpenOrInit(cli, quiet: true);
        var runs = host.Services.History.ReadFrom(0).OfType<RunCompleted>().Select(e => e.Record).ToList();

        if (runs.Count == 0)
        {
            Output.Info("no runs recorded yet.");
            return 0;
        }

        Output.Header("Token economy");

        var byStation = runs.GroupBy(r => r.StationId).OrderBy(g => g.Key).Select(g => new[]
        {
            g.Key,
            g.First().Profile.ToString().ToLowerInvariant(),
            g.Count().ToString(),
            $"{g.Sum(r => (long)r.Usage.BilledInput):N0}",
            $"{g.Sum(r => (long)r.Usage.OutputTokens):N0}",
            $"${g.Sum(r => r.CostUsd):F4}",
            $"{g.Count(r => r.CacheHit)}"
        }).ToList();

        Output.Table(byStation, "station", "profile", "runs", "input", "output", "cost", "cached");

        var thinRuns = runs.Count(r => r.Profile == TokenProfile.Thin && !r.CacheHit);
        var cacheHits = runs.Count(r => r.CacheHit);

        Output.Line();
        Output.Info($"total spend        ${runs.Sum(r => r.CostUsd):F4}");
        Output.Info($"cache hits         {cacheHits} of {runs.Count} runs " +
                    $"({(runs.Count == 0 ? 0 : 100.0 * cacheHits / runs.Count):F0}%)" +
                    (cacheHits > 0 ? $", {TokenEconomy.CacheAvoided(runs):N0} tokens not spent" : ""));

        if (thinRuns > 0)
        {
            Output.Info($"overhead avoided   {TokenEconomy.OverheadAvoided(runs):N0} tokens " +
                        $"across {thinRuns} thin run(s)");
            Output.Step($"  {TokenEconomy.AmbientOverheadTokens:N0} tokens of tool definitions, default preamble, " +
                        $"settings and skills stripped per call");
            Output.Step($"  measured: {TokenEconomy.NaiveBaselineInputTokens:N0} billed input tokens for a default " +
                        $"agent call vs {TokenEconomy.ThinProfileInputTokens} thin " +
                        $"({TokenEconomy.ThinReductionRatio:P1} reduction on identical work)");
        }

        var deterministic = host.Services.State.Items.Values
            .SelectMany(i => i.AcceptanceCriteria)
            .ToList();
        if (deterministic.Count > 0)
        {
            var machine = deterministic.Count(c => c.Verification.IsDeterministic);
            Output.Info($"machine-checked    {machine} of {deterministic.Count} acceptance criteria " +
                        $"({100.0 * machine / deterministic.Count:F0}%) — verified at zero token cost");
        }

        Output.Header("Prompt fitness");
        foreach (var station in new EvolutionService(host).EvolvableStations())
        {
            var stats = Evaluator.ByVersion(runs, station.Id);
            if (stats.Count == 0) continue;

            var champion = host.Services.Prompts.Champion(station.Id);
            Output.Line();
            Output.Info(Output.Bold(station.Id));
            foreach (var s in stats)
            {
                var marker = s.Version == champion.Id ? Output.Green(" ← champion") : "";
                Output.Info($"  {s.Describe}  fitness {Evaluator.Fitness(s, stats[0]):F3}{marker}");
            }
        }
        return 0;
    }

    public static int Prompts(CommandLine cli)
    {
        using var host = OpenOrInit(cli, quiet: true);
        var registry = host.Services.Prompts;

        foreach (var station in host.Blueprint.Stations.Where(s => s.Profile != TokenProfile.None))
        {
            var versions = registry.Versions(station.Id);
            if (versions.Count == 0) continue;

            var pointer = registry.Routing(station.Id);
            Output.Header($"{station.Id}  ({station.Tier}, {station.Profile})");

            foreach (var v in versions)
            {
                var tag = v.Version == pointer.Champion ? Output.Green("champion")
                    : v.Version == pointer.Challenger ? Output.Yellow($"challenger {pointer.ChallengerShare:P0}")
                    : Output.Dim("archived");
                Output.Info($"  v{v.Version}  {tag}  {v.Text.Length:N0} chars");
            }
        }

        if (cli.Get("show") is { Length: > 0 } stationId)
        {
            Output.Header($"{stationId} champion prompt");
            Output.Line(registry.Champion(stationId).Text);
        }
        return 0;
    }

    // ── doctor ──────────────────────────────────────────────────────────────

    /// <summary>Checks whether a deployment is actually able to run: a detectable toolchain,
    /// the claude CLI reachable on PATH, and a blueprint that passes its own validation. Also
    /// surfaces the model usage windows the governor is currently tracking. Exits non-zero if
    /// any check fails, so it can gate CI or a pre-flight script.</summary>
    public static int Doctor(CommandLine cli)
    {
        using var host = OpenOrInit(cli, quiet: true);
        var ok = true;

        Output.Header("Toolchain");
        var toolchain = Toolchain.Detect(host.Paths.RepoRoot);
        if (toolchain.IsEmpty)
        {
            Output.Warn("no toolchain detected — the check station has nothing to run");
            ok = false;
        }
        else
        {
            Output.Success(toolchain.Describe);
        }

        // Reported but never fatal: a stale binary still runs, and refusing to start would be a
        // worse answer than saying which build is running and how to replace it.
        Output.Header("Harness");
        var harness = ProbeHarness(host);
        if (harness.IsStale) Output.Warn(harness.Describe);
        else if (harness.SelfHosted) Output.Success($"built from this repository at {FactoryVersion.Commit}");
        else Output.Info("not built from this repository — nothing to compare");

        Output.Header("Gates");

        // `doctor` dispatches no work, so it is the one command guaranteed to be running while the
        // toolchain is otherwise idle — which is the condition a trustworthy baseline requires.
        if (cli.Has("recapture"))
        {
            Output.Step("recapturing the toolchain baseline — nothing else should be building");
            File.Delete(host.Paths.BaselineFile);
            var captured = ToolchainRunner
                .BaselineAsync(toolchain, host.Paths.RepoRoot, host.Paths.BaselineFile,
                    new GitRepoStateProvider(host.Paths.RepoRoot))
                .GetAwaiter().GetResult();
            Output.Step($"captured at {Short(captured.Commit)}: "
                        + string.Join(", ", captured.Passing.Select(p => $"{p.Key} {(p.Value ? "pass" : "FAIL")}")));
        }

        var baseline = ToolchainRunner.TryLoadBaseline(host.Paths.BaselineFile);
        if (baseline is null)
        {
            Output.Info("no baseline captured yet — every check will block on its first failure");
        }
        else
        {
            foreach (var gate in baseline.DisabledGates)
                Output.Warn($"'{gate}' was recorded as already failing at {Short(baseline.Commit)} — "
                            + "it can no longer block anything. Recapture on an idle machine if that is wrong.");
            foreach (var flaky in baseline.FlakyChecks)
                Output.Warn($"'{flaky}' only passed on a retry — this host's toolchain is unreliable");

            if (baseline.DisabledGates.Count == 0 && baseline.FlakyChecks.Count == 0)
                Output.Success($"all baseline checks passed first time at {Short(baseline.Commit)}");
        }

        Output.Header("Claude CLI");
        var claudeExe = Environment.GetEnvironmentVariable("FACTORY_CLAUDE_BIN") ?? "claude";
        if (Shell.Which(claudeExe))
        {
            Output.Success($"'{claudeExe}' found on PATH");
        }
        else
        {
            Output.Warn($"'{claudeExe}' not found on PATH — model calls will fail to start");
            ok = false;
        }

        Output.Header("Blueprint");
        var errors = host.Blueprint.Validate().ToList();
        if (errors.Count == 0)
        {
            Output.Success($"'{host.Blueprint.Name}' is valid");
        }
        else
        {
            foreach (var e in errors) Output.Warn(e);
            ok = false;
        }

        Output.Header("Usage windows");
        var windows = host.Services.Runner.Governor.Windows;
        if (windows.Count == 0)
            Output.Info("no usage window data recorded yet");
        else
            foreach (var window in windows) Output.Info(window.Describe(DateTimeOffset.UtcNow));

        Output.Line();
        if (ok) Output.Success("all checks passed");
        else Output.Error("one or more checks failed");
        return ok ? 0 : 1;
    }

    // ── help ────────────────────────────────────────────────────────────────

    public static int Help()
    {
        Output.Line($"""
            {Output.Bold("factory")} — an autonomous, self-improving software factory

            {Output.Bold("Deploy and build")}
              factory build "<what you want>"   Deploy if needed, then build it. One prompt.
              factory up [--daemon]             Work the backlog; --daemon keeps watching
              factory init                      Deploy a factory here without starting work

            {Output.Bold("Work")}
              factory intake ["<request>"]      Interactive requirements conversation
              factory add "<title>" [--criterion "<cmd>"]
              factory activate <id> | --all     Promote proposed work into the queue
              factory cancel <id>               Cancel a queued or in-flight item
              factory ls [--all]                Backlog
              factory show <id>                 One item in full, with its run history
              factory status                    Backlog, spend, and configuration
              factory doctor [--recapture]      Check health; --recapture retakes the toolchain baseline

            {Output.Bold("Composition")}
              factory link <path> [--as <name>] [--pipeline]
                                                Link a child factory; --pipeline routes work through it

            {Output.Bold("Self-improvement")}
              factory evolve                    Score prompts, settle trials, propose challengers
              factory prompts [--show <station>] Prompt versions and which is champion
              factory report                    Token economy and prompt fitness

            {Output.Bold("Options")}
              --dir <path>          Operate on another directory
              --budget <usd>        Daily spend ceiling for this run
              --item-budget <usd>   Per-item spend ceiling
              --concurrency <n>     Items in flight at once
              --yes                 Never ask questions; record assumptions instead
              --include-proposed    Also work items that agents proposed themselves
              --evolve / --no-evolve  Force or suppress the evolution pass

            {Output.Dim("Every model call is budgeted, every acceptance criterion is preferred machine-checkable,")}
            {Output.Dim("and every prompt version is scored on real runs before it can become champion.")}
            """);
        return 0;
    }

    public static int Version()
    {
        Output.Line($"factory {FactoryVersion.Full}");
        return 0;
    }

    public static int Unknown(string command)
    {
        Output.Error($"unknown command '{command}'");
        Output.Step("run `factory help`");
        return 1;
    }
}
