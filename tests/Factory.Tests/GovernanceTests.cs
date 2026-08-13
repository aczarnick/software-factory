using Factory.Agents;
using Factory.Runtime;

namespace Factory.Tests;

/// <summary>Controllable clock so usage-window behaviour is tested without waiting.</summary>
internal sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

public class UsageGovernorTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    private static AgentEvent Event(string status, string window = "five_hour", DateTimeOffset? resetsAt = null)
    {
        var reset = (resetsAt ?? Start.AddHours(3)).ToUnixTimeSeconds();
        var json =
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{" +
            "\"status\":\"" + status + "\"," +
            "\"resetsAt\":" + reset + "," +
            "\"rateLimitType\":\"" + window + "\"," +
            "\"overageStatus\":\"rejected\",\"isUsingOverage\":false}}";
        return AgentEvent.TryParse(json)!;
    }

    [Fact]
    public void An_allowed_window_does_not_narrow_the_factory()
    {
        var governor = new UsageGovernor(clock: new FakeClock(Start));
        governor.Observe(Event("allowed"));

        Assert.Equal(4, governor.Concurrency(4));
        Assert.False(governor.ShouldHold(out _, out _));
    }

    [Fact]
    public void A_warning_narrows_concurrency_and_paces_runs()
    {
        var governor = new UsageGovernor(clock: new FakeClock(Start));
        governor.Observe(Event("warning"));

        // Narrow rather than stop: the window is not spent, so work continues more slowly.
        Assert.Equal(1, governor.Concurrency(4));
        Assert.True(governor.ShouldHold(out var wait, out var reason));
        Assert.Equal(UsagePolicy.Default.WarningDelay, wait);
        Assert.Contains("approaching", reason);
    }

    [Fact]
    public void A_rejected_window_holds_until_it_resets()
    {
        var clock = new FakeClock(Start);
        var governor = new UsageGovernor(clock: clock);
        governor.Observe(Event("rejected", resetsAt: Start.AddMinutes(30)));

        Assert.True(governor.ShouldHold(out var wait, out var reason));
        Assert.True(wait >= TimeSpan.FromMinutes(30));
        Assert.Contains("five_hour", reason);
        Assert.Contains("usage limit", reason);
    }

    [Fact]
    public async Task A_wait_longer_than_the_ceiling_stops_rather_than_blocking()
    {
        var governor = new UsageGovernor(
            new UsagePolicy { MaxWait = TimeSpan.FromMinutes(1) }, clock: new FakeClock(Start));

        governor.Observe(Event("rejected", resetsAt: Start.AddHours(4)));

        // A five-hour window must not silently block a command for five hours.
        Assert.False(await governor.AwaitClearanceAsync());
    }

    [Fact]
    public void The_worst_window_binds_when_several_are_known()
    {
        var governor = new UsageGovernor(clock: new FakeClock(Start));
        governor.Observe(Event("allowed", "five_hour"));
        governor.Observe(Event("rejected", "weekly", Start.AddDays(2)));

        Assert.Equal(RateLimitStatus.Rejected, governor.Binding!.Status);
        Assert.Equal("weekly", governor.Binding.Window);
        Assert.Equal(1, governor.Concurrency(8));
    }

    [Fact]
    public void A_window_that_has_reset_no_longer_constrains_anything()
    {
        var clock = new FakeClock(Start);
        var governor = new UsageGovernor(clock: clock);
        governor.Observe(Event("rejected", resetsAt: Start.AddMinutes(10)));

        Assert.True(governor.ShouldHold(out _, out _));

        clock.Advance(TimeSpan.FromMinutes(11));

        Assert.False(governor.ShouldHold(out _, out _));
        Assert.Equal(4, governor.Concurrency(4));
    }

    [Fact]
    public void A_restart_inside_an_exhausted_window_still_knows_it_is_exhausted()
    {
        var dir = TempDir.Create();
        try
        {
            var path = Path.Combine(dir, "usage.json");
            var clock = new FakeClock(Start);

            var first = new UsageGovernor(statePath: path, clock: clock);
            first.Observe(Event("rejected", resetsAt: Start.AddHours(2)));

            // A fresh process must not spend its way back into the same rejection.
            var restarted = new UsageGovernor(statePath: path, clock: clock);
            Assert.True(restarted.ShouldHold(out _, out var reason));
            Assert.Contains("usage limit", reason);
        }
        finally { TempDir.Delete(dir); }
    }

    [Fact]
    public void A_rate_limited_failure_is_treated_as_a_limit_even_without_an_event()
    {
        var governor = new UsageGovernor(clock: new FakeClock(Start));

        governor.ObserveRejection("api error: rate_limit_error");
        Assert.True(governor.ShouldHold(out _, out _));

        var unaffected = new UsageGovernor(clock: new FakeClock(Start));
        unaffected.ObserveRejection("compilation failed");
        Assert.False(unaffected.ShouldHold(out _, out _));
    }

    [Fact]
    public async Task The_runner_holds_a_run_back_when_the_window_is_spent()
    {
        var governor = new UsageGovernor(
            new UsagePolicy { MaxWait = TimeSpan.FromSeconds(1) }, clock: new FakeClock(Start));
        governor.Observe(Event("rejected", resetsAt: Start.AddHours(3)));

        var transport = new FakeTransport().Respond("plan", "should never run");
        var runner = new AgentRunner(transport, cache: null, governor: governor);

        var result = await runner.RunAsync(new AgentRequest
        {
            Prompt = "work",
            Profile = AgentProfile.Thin(Core.ModelTier.Haiku, "sys", "plan")
        });

        Assert.False(result.Success);
        Assert.Contains("usage limit", result.Error);
        Assert.Equal(0, transport.Calls);   // nothing was sent
    }
}

public class ShellTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    [Fact]
    public async Task A_daemon_child_holding_the_pipe_does_not_stall_the_command()
    {
        // Build tools do exactly this: `dotnet build` leaves MSBuild nodes alive that
        // inherited its stdout. Reading to end-of-file therefore waits on the daemon, not on
        // the build — which stalled a check station for its full timeout after the build had
        // already succeeded.
        // The daemon is short-lived on purpose: a long one would outlive the test and slow
        // the whole suite, which is the same antisocial behaviour being tested for.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await Shell.RunAsync("echo finished; sleep 12 &", _dir, timeoutSeconds: 60);
        stopwatch.Stop();

        Assert.True(result.Ok);
        Assert.Contains("finished", result.Stdout);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8),
            $"command returned in {stopwatch.Elapsed.TotalSeconds:F1}s; it should not wait for the daemon");
    }

    [Fact]
    public async Task Output_is_still_captured_for_ordinary_commands()
    {
        var result = await Shell.RunAsync("echo out; echo err 1>&2; exit 3", _dir, timeoutSeconds: 30);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("out", result.Stdout);
        Assert.Contains("err", result.Stderr);
    }

    [Fact]
    public void Which_finds_a_present_tool_and_not_an_absent_one()
    {
        Assert.True(Shell.Which("sh"));
        Assert.False(Shell.Which("definitely-not-a-real-tool-xyz"));
    }
}

public class ToolchainTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    [Fact]
    public void A_dotnet_project_is_detected_with_a_build_check()
    {
        File.WriteAllText(Path.Combine(_dir, "App.csproj"), "<Project/>");

        var toolchain = Toolchain.Detect(_dir);

        Assert.Equal("dotnet", toolchain.Name);
        Assert.Contains(toolchain.Checks, c => c.Name == "build");
    }

    [Fact]
    public void A_node_project_declares_its_own_checks_rather_than_being_guessed_at()
    {
        File.WriteAllText(Path.Combine(_dir, "package.json"),
            """{"name":"x","scripts":{"build":"tsc","lint":"eslint ."}}""");

        var toolchain = Toolchain.Detect(_dir);

        Assert.Equal("node", toolchain.Name);
        Assert.Contains(toolchain.Checks, c => c.Name == "build");
        Assert.Contains(toolchain.Checks, c => c.Name == "lint");
        // No test script declared, so no test check is invented.
        Assert.DoesNotContain(toolchain.Checks, c => c.Name == "test");
    }

    [Fact]
    public void A_repository_with_no_recognisable_toolchain_yields_nothing()
    {
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "hello");
        Assert.True(Toolchain.Detect(_dir).IsEmpty);
    }

    [Fact]
    public async Task Checks_run_in_order_and_a_failed_build_stops_the_cascade()
    {
        var toolchain = new Toolchain
        {
            Name = "fake",
            Checks =
            [
                new ToolchainCheck("build", "exit 1", 30),
                new ToolchainCheck("test", "exit 0", 30)
            ]
        };

        var results = await ToolchainRunner.RunAsync(toolchain, _dir);

        // Running the tests after a failed build would report consequences, not causes.
        Assert.Single(results);
        Assert.False(results[0].Passed);
        Assert.Contains("exited 1", results[0].Detail);
    }

    [Fact]
    public void Only_regressions_block_and_pre_existing_failures_are_excused()
    {
        var baseline = new ToolchainBaseline
        {
            Passing = new Dictionary<string, bool> { ["build"] = true, ["lint"] = false }
        };

        var verdict = ToolchainRunner.Compare(
        [
            new CheckOutcome("build", false, "build broke", 10),
            new CheckOutcome("lint", false, "lint still unhappy", 10)
        ], baseline);

        // The linter was already failing when the factory arrived; that is not this item's doing.
        Assert.False(verdict.Passed);
        Assert.Single(verdict.Regressions);
        Assert.Equal("build", verdict.Regressions[0].Name);
        Assert.Single(verdict.PreExisting);
        Assert.Equal("lint", verdict.PreExisting[0].Name);
    }

    [Fact]
    public void A_check_that_did_not_exist_at_baseline_must_pass()
    {
        var verdict = ToolchainRunner.Compare(
            [new CheckOutcome("test", false, "new suite fails", 10)],
            new ToolchainBaseline { Passing = new Dictionary<string, bool> { ["build"] = true } });

        // A test suite introduced by this work failing means the work is not demonstrated.
        Assert.False(verdict.Passed);
        Assert.Single(verdict.Regressions);
    }

    [Fact]
    public void Everything_passing_is_a_pass()
    {
        var verdict = ToolchainRunner.Compare(
            [new CheckOutcome("build", true, "ok", 10), new CheckOutcome("test", true, "ok", 20)],
            new ToolchainBaseline { Passing = new Dictionary<string, bool> { ["build"] = true, ["test"] = true } });

        Assert.True(verdict.Passed);
        Assert.Contains("2/2", verdict.Summary);
    }
}
