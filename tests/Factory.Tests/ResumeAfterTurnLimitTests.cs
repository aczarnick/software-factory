using Factory.Agents;
using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

/// <summary>
/// A station that exhausts its turns mid-change has not failed at anything — it ran out of room.
/// Restarting it throws away a conversation that was nearly finished and pays for the whole thing
/// again: one such run cost $1.92 of a $2.00 ceiling and needed $0.32 more to finish, while the
/// restart cost another $2. The transport has always accepted a session to resume; nothing set it.
/// </summary>
public class ResumeAfterTurnLimitTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    private const string SingleChild =
        """{"children":[{"key":"a","title":"do it","kind":"Feature","requirements":["works"],"acceptanceCriteria":[]}]}""";

    private const string Plan =
        """{"files":[{"path":"hello.txt","change":"create"}],"steps":["write the file"],"risks":[]}""";

    /// <summary>Scripts implement to exhaust its turns once, then succeed, so the second request
    /// can be inspected for the session the first one left behind.</summary>
    private static FakeTransport ExhaustsThenSucceeds(string sessionId, string produces = "hello.txt")
    {
        var attempts = 0;
        return new FakeTransport()
            .Respond("decompose", SingleChild)
            .Respond("plan", Plan)
            .Respond("implement", request =>
            {
                if (++attempts == 1) return FakeTransport.OutOfTurns(sessionId);

                File.WriteAllText(Path.Combine(request.WorkingDirectory!, produces), "hi\n");
                return FakeTransport.Success("finished what I started", cost: 0.02m);
            });
    }

    private static AgentRequest[] ImplementRequests(FakeTransport transport) =>
        [.. transport.Requests.Where(r => r.Profile.Name == "implement")];

    [Fact]
    public async Task A_station_that_ran_out_of_turns_resumes_its_own_session()
    {
        var transport = ExhaustsThenSucceeds("sess-abc");
        using var host = FactoryHost.Init(_dir, transport: transport);

        host.Submit(WorkItem.Create("create hello.txt") with
        {
            AcceptanceCriteria = [AcceptanceCriterion.Command("file exists", "test -f hello.txt")]
        });

        await host.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true });

        var implement = ImplementRequests(transport);
        Assert.True(implement.Length >= 2, "implement should have been attempted again");
        Assert.Null(implement[0].ResumeSessionId);
        Assert.Equal("sess-abc", implement[1].ResumeSessionId);
    }

    [Fact]
    public async Task A_resumed_attempt_does_not_resend_the_whole_briefing()
    {
        var transport = ExhaustsThenSucceeds("sess-abc");
        using var host = FactoryHost.Init(_dir, transport: transport);

        host.Submit(WorkItem.Create("create hello.txt") with
        {
            AcceptanceCriteria = [AcceptanceCriterion.Command("file exists", "test -f hello.txt")]
        });

        await host.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true });

        var implement = ImplementRequests(transport);

        // The saving is the point: the resumed turn continues a conversation that already holds the
        // item, the plan and everything read so far. Re-sending them would pay for it twice.
        Assert.True(implement[1].Prompt.Length < implement[0].Prompt.Length,
            "a resumed attempt should continue, not restate the briefing");
    }

    [Fact]
    public async Task A_run_that_failed_for_any_other_reason_is_not_resumed()
    {
        var attempts = 0;
        var transport = new FakeTransport()
            .Respond("decompose", SingleChild)
            .Respond("plan", Plan)
            .Respond("implement", request =>
            {
                if (++attempts == 1) return AgentRunResult.Failure("the model refused");

                File.WriteAllText(Path.Combine(request.WorkingDirectory!, "hello.txt"), "hi\n");
                return FakeTransport.Success("done", cost: 0.02m);
            });

        using var host = FactoryHost.Init(_dir, transport: transport);
        host.Submit(WorkItem.Create("create hello.txt") with
        {
            AcceptanceCriteria = [AcceptanceCriterion.Command("file exists", "test -f hello.txt")]
        });

        await host.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true });

        // Resuming a conversation that ended for a reason of its own would carry the reason with it.
        Assert.All(ImplementRequests(transport), r => Assert.Null(r.ResumeSessionId));
    }

    [Fact]
    public async Task A_session_is_never_resumed_by_a_different_station()
    {
        var transport = ExhaustsThenSucceeds("sess-abc");
        using var host = FactoryHost.Init(_dir, transport: transport);

        host.Submit(WorkItem.Create("create hello.txt") with
        {
            AcceptanceCriteria = [AcceptanceCriterion.Command("file exists", "test -f hello.txt")]
        });

        await host.CreateOrchestrator().RunAsync(new OrchestratorOptions { StopWhenIdle = true });

        // Every station has its own conversation; handing one station's session to another would
        // splice two different briefings together.
        Assert.All(transport.Requests.Where(r => r.Profile.Name != "implement"),
            r => Assert.Null(r.ResumeSessionId));
    }
}
