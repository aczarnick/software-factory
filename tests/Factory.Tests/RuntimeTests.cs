using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

public class DeterministicVerifierTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    [Fact]
    public async Task Passing_and_failing_commands_are_reported_accurately()
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
    public async Task File_existence_is_checked_against_the_workspace()
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
    public async Task Judged_criteria_are_deferred_rather_than_guessed_at()
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
    public async Task Stdout_matching_is_enforced()
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
    public async Task Runaway_commands_are_killed_and_reported_as_failures()
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

public class PipelineTests : IDisposable
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
    public async Task An_item_flows_from_ready_to_done_and_lands_on_the_mainline()
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
    public async Task Review_is_skipped_when_every_criterion_was_machine_checked()
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
    public async Task A_failing_gate_routes_back_to_implementation_with_the_failure_attached()
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
    public async Task Decomposition_into_several_children_files_them_and_closes_the_parent()
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
        Assert.Equal(WorkItemState.Done, host.Services.State.Items[parent.Id].State);

        // The declared ordering must survive the key-to-id mapping.
        var second = children.First(c => c.Title == "second");
        var first = children.First(c => c.Title == "first");
        Assert.Equal([first.Id], second.DependsOn);
    }

    [Fact]
    public async Task Work_agents_file_about_their_own_observations_lands_as_a_proposal()
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
    }

    [Fact]
    public async Task Exhausting_the_budget_blocks_the_item_instead_of_overspending()
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
    public async Task Orphaned_work_is_requeued_after_a_restart()
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
    public void Deploying_twice_preserves_history()
    {
        using (var first = FactoryHost.Init(_dir, transport: new FakeTransport()))
            first.Submit(WorkItem.Create("earlier work"));

        using var second = FactoryHost.Init(_dir, transport: new FakeTransport());
        Assert.Single(second.Services.State.Items);
    }
}

public class CompositionTests : IDisposable
{
    private readonly string _parent = TempDir.Create();
    private readonly string _child = TempDir.Create();

    public void Dispose()
    {
        TempDir.Delete(_parent);
        TempDir.Delete(_child);
    }

    [Fact]
    public async Task A_parent_factory_delegates_an_item_to_its_child_and_rolls_up_the_cost()
    {
        var childTransport = new FakeTransport()
            .Respond("decompose",
                """{"children":[{"key":"a","title":"do it","kind":"Feature","requirements":["works"],"acceptanceCriteria":[]}]}""")
            .Respond("plan", """{"files":[{"path":"built.txt","change":"create"}],"steps":["write"],"risks":[]}""")
            .Respond("implement", request =>
            {
                File.WriteAllText(Path.Combine(request.WorkingDirectory!, "built.txt"), "done\n");
                return FakeTransport.Success("built", cost: 0.05m);
            });

        using (var child = FactoryHost.Init(_child, transport: childTransport)) { }

        var composite = Blueprint.Composite("platform", new Dictionary<string, string> { ["worker"] = _child });
        using var parent = FactoryHost.Init(_parent, composite,
            new FactoryConfig
            {
                Name = "platform",
                Factories = new Dictionary<string, string> { ["worker"] = _child }
            },
            transport: new FakeTransport().Respond("decompose",
                """{"children":[{"key":"a","title":"delegated work","kind":"Feature","requirements":["works"],"acceptanceCriteria":[]}]}"""));

        parent.Submit(WorkItem.Create("build it") with
        {
            AcceptanceCriteria = [AcceptanceCriterion.Command("exists", "test -f built.txt")]
        });

        // The child factory must be the one that actually runs, using its own transport.
        var report = await parent.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true });

        Assert.Equal(1, report.Completed);
        Assert.True(File.Exists(Path.Combine(_child, "built.txt")), "the child factory should have built it");

        var delegations = parent.Services.Ledger.ReadAll().OfType<DelegationCompleted>().ToList();
        Assert.Single(delegations);
        Assert.True(delegations[0].Success);
        Assert.True(delegations[0].ChildCostUsd > 0, "child spend should roll up to the parent");
    }

    [Fact]
    public async Task Delegation_depth_is_bounded_so_a_factory_containing_itself_cannot_run_away()
    {
        var blueprint = Blueprint.Composite("loop", new Dictionary<string, string> { ["self"] = _parent })
            with { MaxDelegationDepth = 0 };

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
