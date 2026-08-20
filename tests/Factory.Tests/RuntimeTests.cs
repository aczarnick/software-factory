using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

public sealed class DeterministicVerifierTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    [Fact]
    public async Task PassingAndFailingCommandsAreReportedAccurately()
    {
        var item = WorkItem.Create("thing") with
        {
            AcceptanceCriteria =
            [
                AcceptanceCriterion.Command("exits zero", "exit 0"),
                AcceptanceCriterion.Command("exits one", "exit 1")
            ]
        };

        var outcome = await DeterministicVerifier.VerifyAsync(item, _dir);

        Assert.False(outcome.DeterministicPassed);
        Assert.Single(outcome.Report.Failures);
        Assert.Contains("exited 1", outcome.Report.Summary);
    }

    [Fact]
    public async Task FileExistenceIsCheckedAgainstTheWorkspace()
    {
        File.WriteAllText(Path.Combine(_dir, "present.txt"), "hi");

        var item = WorkItem.Create("thing") with
        {
            AcceptanceCriteria =
            [
                AcceptanceCriterion.FileExists("present", "present.txt"),
                AcceptanceCriterion.FileExists("absent", "absent.txt")
            ]
        };

        var outcome = await DeterministicVerifier.VerifyAsync(item, _dir);

        Assert.True(outcome.Report.Results[0].Passed);
        Assert.False(outcome.Report.Results[1].Passed);
    }

    [Fact]
    public async Task JudgedCriteriaAreDeferredRatherThanGuessedAt()
    {
        var item = WorkItem.Create("thing") with
        {
            AcceptanceCriteria =
            [
                AcceptanceCriterion.Command("builds", "exit 0"),
                AcceptanceCriterion.Judged("reads well", "prose is clear")
            ]
        };

        var outcome = await DeterministicVerifier.VerifyAsync(item, _dir);

        Assert.True(outcome.DeterministicPassed);
        Assert.Single(outcome.Deferred);
        Assert.Equal("reads well", outcome.Deferred[0].Statement);
    }

    [Fact]
    public async Task StdoutMatchingIsEnforced()
    {
        var item = WorkItem.Create("thing") with
        {
            AcceptanceCriteria =
            [
                new AcceptanceCriterion
                {
                    Id = "ac1", Statement = "greets",
                    Verification = new CommandVerification("echo hello", 0, "hello")
                },
                new AcceptanceCriterion
                {
                    Id = "ac2", Statement = "greets differently",
                    Verification = new CommandVerification("echo hello", 0, "goodbye")
                }
            ]
        };

        var outcome = await DeterministicVerifier.VerifyAsync(item, _dir);

        Assert.True(outcome.Report.Results[0].Passed);
        Assert.False(outcome.Report.Results[1].Passed);
    }

    [Fact]
    public async Task RunawayCommandsAreKilledAndReportedAsFailures()
    {
        var item = WorkItem.Create("thing") with
        {
            AcceptanceCriteria =
            [
                new AcceptanceCriterion
                {
                    Id = "ac1", Statement = "never finishes",
                    Verification = new CommandVerification("sleep 30", 0, null, TimeoutSeconds: 1)
                }
            ]
        };

        var outcome = await DeterministicVerifier.VerifyAsync(item, _dir);

        Assert.False(outcome.DeterministicPassed);
        Assert.Contains("timed out", outcome.Report.Summary);
    }
}

public sealed class PipelineTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    private const string SingleChild =
        """{"children":[{"key":"a","title":"do it","kind":"Feature","requirements":["works"],"acceptanceCriteria":[]}]}""";

    private const string Plan =
        """{"files":[{"path":"hello.txt","change":"create"}],"steps":["write the file"],"risks":[]}""";

    private FactoryHost Open(FakeTransport transport) =>
        FactoryHost.Init(_dir, transport: transport);

    private static FakeTransport Scripted(string produces = "hello.txt")
    {
        return new FakeTransport()
            .Respond("decompose", SingleChild)
            .Respond("plan", Plan)
            .Respond("implement", request =>
            {
                // A real implementation station edits files; the fake does the same so the
                // downstream deterministic gates have something genuine to check.
                File.WriteAllText(Path.Combine(request.WorkingDirectory!, produces), "hi\n");
                return FakeTransport.Success("wrote the file", cost: 0.02m);
            });
    }

    [Fact]
    public async Task AnItemFlowsFromReadyToDoneAndLandsOnTheMainline()
    {
        using var host = Open(Scripted());

        host.Submit(WorkItem.Create("create hello.txt") with
        {
            AcceptanceCriteria = [AcceptanceCriterion.Command("file exists", "test -f hello.txt")]
        });

        var report = await host.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true });

        Assert.Equal(1, report.Completed);
        Assert.Equal(0, report.Failed);
        Assert.True(File.Exists(Path.Combine(_dir, "hello.txt")), "integrated work should be on the mainline");
        Assert.All(host.Services.State.Items.Values, i => Assert.Equal(WorkItemState.Done, i.State));
    }

    [Fact]
    public async Task TwoIndependentReadyItemsAreBothClaimedAndCompleted()
    {
        // Each item must produce its own file: two items racing to write the same path would
        // let a lucky merge order mask a claim that never actually ran.
        var transport = Scripted().Respond("implement", request =>
        {
            var produces = request.Prompt.Contains("second thing") ? "second.txt" : "hello.txt";
            File.WriteAllText(Path.Combine(request.WorkingDirectory!, produces), "hi\n");
            return FakeTransport.Success("wrote the file", cost: 0.02m);
        });
        using var host = Open(transport);

        var first = host.Submit(WorkItem.Create("create hello.txt") with
        {
            AcceptanceCriteria = [AcceptanceCriterion.Command("file exists", "test -f hello.txt")]
        });
        var second = host.Submit(WorkItem.Create("create second thing") with
        {
            AcceptanceCriteria = [AcceptanceCriterion.Command("file exists", "test -f second.txt")]
        });

        var report = await host.CreateOrchestrator()
            .RunAsync(new OrchestratorOptions { MaxConcurrency = 2, StopWhenIdle = true });

        // Asserted before the count, because this test is the suite's known flake -- about one run in
        // nine, `Expected 2 / Actual 1` -- and a count alone cannot say whether the second item was
        // never claimed, failed, or blocked. These two name the failure mode instead of leaving the
        // next occurrence to be re-run and forgotten, which is how three criticals survived a green
        // suite on the previous phase. The flake itself is deferred and recorded in the phase-4
        // handoff note with its measured rate and mechanism.
        Assert.Equal(0, report.Failed);
        Assert.Equal(0, report.Blocked);

        Assert.Equal(2, report.Completed);
        Assert.Equal(WorkItemState.Done, host.Services.State.Items[first.Id].State);
        Assert.Equal(WorkItemState.Done, host.Services.State.Items[second.Id].State);
    }

    [Fact]
    public async Task ReviewIsSkippedWhenEveryCriterionWasMachineChecked()
    {
        var transport = Scripted();
        using var host = Open(transport);

        host.Submit(WorkItem.Create("create hello.txt") with
        {
            AcceptanceCriteria = [AcceptanceCriterion.Command("file exists", "test -f hello.txt")]
        });

        await host.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true });

        // The commands already proved the work; a model call here would add cost and no information.
        Assert.DoesNotContain(transport.Requests, r => r.Profile.Name == "review");
    }

    [Fact]
    public async Task AFailingGateRoutesBackToImplementationWithTheFailureAttached()
    {
        var transport = Scripted(produces: "wrong-name.txt");
        using var host = Open(transport);

        host.Submit(WorkItem.Create("create hello.txt") with
        {
            AcceptanceCriteria = [AcceptanceCriterion.Command("file exists", "test -f hello.txt")]
        });

        var report = await host.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true });

        Assert.Equal(1, report.Failed);

        var implementCalls = transport.Requests.Count(r => r.Profile.Name == "implement");
        Assert.True(implementCalls > 1, $"expected a retry, saw {implementCalls} implement call(s)");

        // The retry must carry the reason, otherwise the station cannot learn from it.
        Assert.Contains(transport.Requests.Where(r => r.Profile.Name == "implement"),
            r => r.Prompt.Contains("previous attempt was rejected"));
    }

    [Fact]
    public async Task DecompositionIntoSeveralChildrenFilesThemAndSupersedesTheParent()
    {
        var transport = Scripted().Respond("decompose",
            """
            {"children":[
              {"key":"a","title":"first","kind":"Feature","requirements":["x"],"acceptanceCriteria":[]},
              {"key":"b","title":"second","kind":"Feature","requirements":["y"],"acceptanceCriteria":[],"dependsOn":["a"]}
            ]}
            """);

        using var host = Open(transport);
        var parent = host.Submit(WorkItem.Create("a big thing"));

        await host.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true, MaxItems = 1 });

        var children = host.Services.State.Children(parent.Id);
        Assert.Equal(2, children.Count);

        // The parent's own acceptance criteria were never run — its children carry the work now — so
        // it must not be reported as Done. Done here would be a false green in every backlog view.
        Assert.Equal(WorkItemState.Superseded, host.Services.State.Items[parent.Id].State);

        // The declared ordering must survive the key-to-id mapping.
        var second = children.First(c => c.Title == "second");
        var first = children.First(c => c.Title == "first");
        Assert.Equal([first.Id], second.DependsOn);
    }

    [Fact]
    public async Task WorkAgentsFileAboutTheirOwnObservationsLandsAsAProposal()
    {
        var transport = Scripted()
            .Respond("decompose",
                """{"children":[{"key":"a","title":"do it","kind":"Feature","requirements":["works"],"acceptanceCriteria":[{"statement":"looks right","verification":{"kind":"judge","rubric":"is it sensible"}}]}]}""")
            .Respond("review",
                """{"pass":true,"summary":"fine","findings":[],"followUp":["add a regression test"]}""");

        using var host = Open(transport);

        host.Submit(WorkItem.Create("create hello.txt") with
        {
            AcceptanceCriteria =
            [
                AcceptanceCriterion.Command("file exists", "test -f hello.txt"),
                AcceptanceCriterion.Judged("looks right", "is it sensible")
            ]
        });

        await host.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true });

        var proposed = host.Services.State.Items.Values
            .Where(i => i.State == WorkItemState.Draft && i.Provenance.Kind == ProvenanceKind.Agent)
            .ToList();

        // Filed, but not queued: a single request must not snowball into unbounded self-directed work.
        Assert.Single(proposed);
        Assert.Equal("add a regression test", proposed[0].Title);

        // Work a station files about work it was already doing sorts after its subject, and stays
        // inside the band the backlog store accepts.
        Assert.Equal(Priorities.Below(Priorities.Default), proposed[0].Priority);
    }

    [Fact]
    public async Task ExhaustingTheBudgetBlocksTheItemInsteadOfOverspending()
    {
        using var host = FactoryHost.Init(_dir, transport: Scripted());

        // A ceiling far below what the first station costs.
        host.Services.Budget.Record(WorkItem.Create("x"), 0m);
        var item = host.Submit(WorkItem.Create("create hello.txt") with { BudgetUsd = 0.0m });

        var report = await host.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true });

        Assert.Equal(1, report.Blocked);
        Assert.Equal(WorkItemState.Blocked, host.Services.State.Items[item.Id].State);
    }

    [Fact]
    public async Task ADirtyMainlineBlocksIntegrationAndKeepsTheVerifiedWork()
    {
        using var host = Open(Scripted());
        await host.Services.Workspace.EnsureRepoAsync();

        // A tracked file the user is midway through editing.
        var userFile = Path.Combine(_dir, "user-edit.txt");
        File.WriteAllText(userFile, "committed\n");
        await Shell.GitAsync(_dir, default, "add", "-A");
        await Shell.GitAsync(_dir, default, "commit", "-q", "-m", "user work");
        File.WriteAllText(userFile, "half-finished edit\n");

        var item = host.Submit(WorkItem.Create("create hello.txt") with
        {
            AcceptanceCriteria = [AcceptanceCriterion.Command("file exists", "test -f hello.txt")]
        });

        var report = await host.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true });

        // Blocked, not failed: the work passed its gates and only integration is waiting.
        Assert.Equal(1, report.Blocked);
        Assert.Equal(0, report.Failed);
        Assert.Equal(WorkItemState.Blocked, host.Services.State.Items[item.Id].State);

        // The user's in-progress edit must survive untouched.
        Assert.Equal("half-finished edit\n", File.ReadAllText(userFile));

        // And the verified work must not have been thrown away.
        Assert.True(Directory.Exists(Path.Combine(host.Paths.WorktreesDir, item.Id)),
            "a blocked item must keep its worktree so nothing verified is redone");

        var reason = host.Services.State.Items[item.Id].LastError ?? "";
        Assert.Contains("uncommitted changes", reason);
        Assert.Contains("factory activate", reason);
    }

    [Fact]
    public async Task OrphanedWorkIsRequeuedAfterARestart()
    {
        using var host = Open(Scripted());

        var item = host.Submit(WorkItem.Create("interrupted"));
        var running = host.Transition(item, WorkItemState.InProgress, "pretend crash");
        host.Update(running);

        Assert.Empty(host.Services.State.Dispatchable());

        // Starting the orchestrator recovers it rather than losing it.
        await host.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true, MaxItems = 0 });

        Assert.Equal(WorkItemState.Ready, host.Services.State.Items[item.Id].State);
    }

    [Fact]
    public void DeployingTwicePreservesHistory()
    {
        using (var first = FactoryHost.Init(_dir, transport: new FakeTransport()))
            first.Submit(WorkItem.Create("earlier work"));

        using var second = FactoryHost.Init(_dir, transport: new FakeTransport());
        Assert.Single(second.Services.State.Items);
    }
}

public sealed class HeartbeatStoppedTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    [Fact]
    public void HeartbeatStoppedOnDispose()
    {
        using var host = FactoryHost.Init(_dir, transport: new FakeTransport());
        var orchestrator = host.CreateOrchestrator();

        orchestrator.Dispose();

        var json = File.ReadAllText(host.Paths.StatusFile);
        var status = FactoryJson.Read<HeartbeatStatus>(json);

        Assert.Equal("stopped", status!.Status);
        Assert.NotNull(status.StoppedAtUtc);
    }
}

public sealed class OrchestratorStallThresholdTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    [Fact]
    public void StallThresholdDefaultsTo120Seconds()
    {
        using var host = FactoryHost.Init(_dir, transport: new FakeTransport());
        using var orchestrator = new Orchestrator(host);

        Assert.Equal(TimeSpan.FromSeconds(120), orchestrator.StallThreshold);
    }

    [Fact]
    public void StallThresholdIsSettableViaTheConstructor()
    {
        using var host = FactoryHost.Init(_dir, transport: new FakeTransport());
        using var orchestrator = new Orchestrator(host, TimeSpan.FromSeconds(45));

        Assert.Equal(TimeSpan.FromSeconds(45), orchestrator.StallThreshold);
    }
}

public sealed class DotnetToolchainRequirementReaderTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    private readonly DotnetToolchainRequirementReader _reader = new();

    [Fact]
    public async Task GlobalJsonSdkVersionIsExtracted()
    {
        File.WriteAllText(Path.Combine(_dir, "global.json"), """{"sdk":{"version":"8.0.100"}}""");

        var requirement = await _reader.ReadRequirementsAsync(_dir);

        Assert.Equal(["8.0.100"], requirement.RequiredSdkVersions);
    }

    [Fact]
    public async Task AMissingGlobalJsonYieldsNoRequiredSdkVersionRatherThanThrowing()
    {
        var requirement = await _reader.ReadRequirementsAsync(_dir);

        Assert.Empty(requirement.RequiredSdkVersions);
    }

    [Fact]
    public async Task AGlobalJsonWithoutSdkVersionYieldsNoRequiredSdkVersion()
    {
        File.WriteAllText(Path.Combine(_dir, "global.json"), """{"sdk":{"rollForward":"latestMinor"}}""");

        var requirement = await _reader.ReadRequirementsAsync(_dir);

        Assert.Empty(requirement.RequiredSdkVersions);
    }

    [Fact]
    public async Task SingleTargetFrameworkIsReadFromACsproj()
    {
        File.WriteAllText(Path.Combine(_dir, "a.csproj"),
            "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");

        var requirement = await _reader.ReadRequirementsAsync(_dir);

        Assert.Equal(["net8.0"], requirement.TargetFrameworks);
    }

    [Fact]
    public async Task SemicolonSeparatedTargetFrameworksAreSplitAndDeduplicatedAcrossFiles()
    {
        File.WriteAllText(Path.Combine(_dir, "multi.csproj"),
            "<Project><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(_dir, "single.csproj"),
            "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");

        var requirement = await _reader.ReadRequirementsAsync(_dir);

        Assert.Equal(2, requirement.TargetFrameworks.Count);
        Assert.Contains("net8.0", requirement.TargetFrameworks);
        Assert.Contains("net9.0", requirement.TargetFrameworks);
    }
}

public sealed class CompositionTests : IDisposable
{
    private readonly string _parent = TempDir.Create();
    private readonly string _child = TempDir.Create();

    public void Dispose()
    {
        TempDir.Delete(_parent);
        TempDir.Delete(_child);
    }

    [Fact]
    public async Task AParentFactoryDelegatesAnItemToItsChildAndRollsUpTheCost()
    {
        // One transport serves the whole composite, as it does in production: the child
        // inherits it from the parent rather than silently opening a default one.
        var transport = new FakeTransport()
            .Respond("decompose",
                """{"children":[{"key":"a","title":"do it","kind":"Feature","requirements":["works"],"acceptanceCriteria":[]}]}""")
            .Respond("plan", """{"files":[{"path":"built.txt","change":"create"}],"steps":["write"],"risks":[]}""")
            .Respond("implement", request =>
            {
                File.WriteAllText(Path.Combine(request.WorkingDirectory!, "built.txt"), "done\n");
                return FakeTransport.Success("built", cost: 0.05m);
            });

        using (var child = FactoryHost.Init(_child, transport: transport)) { }

        var composite = Blueprint.Composite("platform", new Dictionary<string, string> { ["worker"] = _child });
        using var parent = FactoryHost.Init(_parent, composite,
            new FactoryConfig
            {
                Name = "platform",
                Factories = new Dictionary<string, string> { ["worker"] = _child }
            },
            transport: transport);

        parent.Submit(WorkItem.Create("build it") with
        {
            AcceptanceCriteria = [AcceptanceCriterion.Command("exists", "test -f built.txt")]
        });

        // The child factory must be the one that actually runs, using its own transport.
        var report = await parent.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true });

        Assert.Equal(1, report.Completed);
        Assert.True(File.Exists(Path.Combine(_child, "built.txt")), "the child factory should have built it");

        var delegations = parent.Services.History.ReadFrom(0).OfType<DelegationCompleted>().ToList();
        Assert.Single(delegations);
        Assert.True(delegations[0].Success);
        Assert.True(delegations[0].ChildCostUsd > 0, "child spend should roll up to the parent");

        // A composite that hides its children's spend makes every budget figure a lie.
        Assert.Equal(delegations[0].ChildCostUsd, report.DelegatedCostUsd);
        Assert.True(report.CostUsd >= delegations[0].ChildCostUsd,
            $"headline cost ${report.CostUsd:F4} must include the ${delegations[0].ChildCostUsd:F4} spent downstream");
    }

    [Fact]
    public async Task DelegationDepthIsBoundedSoAFactoryContainingItselfCannotRunAway()
    {
        var blueprint = Blueprint.Composite("loop", new Dictionary<string, string> { ["self"] = _parent })
            with
        { MaxDelegationDepth = 0 };

        using var host = FactoryHost.Init(_parent, blueprint,
            new FactoryConfig { Name = "loop", Factories = new Dictionary<string, string> { ["self"] = _parent } },
            transport: new FakeTransport().Respond("decompose",
                """{"children":[{"key":"a","title":"recursive","kind":"Feature","requirements":["x"],"acceptanceCriteria":[]}]}"""));

        host.Submit(WorkItem.Create("recurse forever"));

        var report = await host.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true });

        Assert.Equal(0, report.Completed);
        Assert.True(report.Failed + report.Blocked > 0);
    }
}

public sealed class RemediationRunnerTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    private sealed class FakeRemediationRunner : IRemediationRunner
    {
        public Task<RemediationResult> RemediateAsync(ToolchainRequirement requirement, CancellationToken ct = default) =>
            Task.FromResult(new RemediationResult(Found: true, Attempted: true, Succeeded: true, "fixed it", null));
    }

    [Fact]
    public async Task AFakeRunnerCanStandInViaConstructorInjection()
    {
        IRemediationRunner runner = new FakeRemediationRunner();

        var result = await runner.RemediateAsync(new ToolchainRequirement("dotnet"));

        Assert.True(result.Found);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task DefaultRunnerReportsNotFoundWhenNoScriptIsPresent()
    {
        var runner = new DefaultRemediationRunner(_dir);

        var result = await runner.RemediateAsync(new ToolchainRequirement("dotnet"));

        Assert.False(result.Found);
        Assert.False(result.Attempted);
    }

    [Fact]
    public async Task DefaultRunnerExecutesTheDiscoveredInstallScript()
    {
        File.WriteAllText(Path.Combine(_dir, "install.sh"), "#!/bin/sh\necho remediated\n");
        var runner = new DefaultRemediationRunner(_dir);

        var result = await runner.RemediateAsync(new ToolchainRequirement("dotnet"));

        Assert.True(result.Found);
        Assert.True(result.Succeeded);
        Assert.Contains("remediated", result.Output);
    }
}

public class ClaimRefreshTests
{
    /// <summary>Refuses a heartbeat for one named id and records the rest, wrapped in the guard the
    /// host always composes so the exception the loop meets is the one production raises.</summary>
    private sealed class PoisonsOneHeartbeat(string poisonedId) : IWorkItemStore
    {
        public List<string> Refreshed { get; } = [];

        public void Heartbeat(string id)
        {
            if (id == poisonedId) throw new InvalidOperationException($"no lease for {id}");

            Refreshed.Add(id);
        }

        public WorkItem Add(WorkItem item) => throw new NotSupportedException();
        public WorkItem Update(WorkItem item) => throw new NotSupportedException();
        public WorkItem Transition(WorkItem item, WorkItemState to, string? reason) => throw new NotSupportedException();
        public WorkItem? Get(string id) => throw new NotSupportedException();
        public IReadOnlyList<WorkItem> All() => throw new NotSupportedException();
        public WorkItem? TryClaim(string owner) => throw new NotSupportedException();
        public void Release(string id, string reason) => throw new NotSupportedException();
        public void Sync() => throw new NotSupportedException();
        public IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan) => throw new NotSupportedException();
    }

    [Fact]
    public void OneClaimTheBacklogWillNotRefreshDoesNotCostTheOthersTheirs()
    {
        var inner = new PoisonsOneHeartbeat("wi-sick");
        var logged = new List<string>();

        // Order matters: the sick id is first, so an unguarded loop never reaches the healthy one. At
        // concurrency 2 that is one item costing the other its refresh every tick, and the lease it
        // was holding then expires while its station is still working.
        Orchestrator.RefreshEachClaim(
            new GuardedWorkItemStore(inner, "beads"), ["wi-sick", "wi-healthy"], logged.Add);

        Assert.Equal(["wi-healthy"], inner.Refreshed);
        Assert.Contains(logged, message => message.Contains("wi-sick"));
    }
}
