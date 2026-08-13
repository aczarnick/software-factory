using Factory.Agents;
using Factory.Core;

namespace Factory.Tests;

public class AgentProfileTests
{
    private static string ArgValue(List<string> args, string flag)
    {
        var i = args.IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : "";
    }

    [Fact]
    public void Thin_strips_tools_settings_skills_and_mcp()
    {
        var args = AgentProfile.Thin(ModelTier.Haiku, "You classify things.").ToArgs();

        Assert.Equal("", ArgValue(args, "--tools"));
        Assert.Equal("You classify things.", ArgValue(args, "--system-prompt"));
        Assert.Equal("", ArgValue(args, "--setting-sources"));
        Assert.Contains("--disable-slash-commands", args);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Equal("haiku", ArgValue(args, "--model"));
    }

    [Fact]
    public void Thick_keeps_the_default_preamble_so_the_cache_prefix_stays_stable()
    {
        var args = AgentProfile.Thick(ModelTier.Sonnet, ["Read", "Edit", "Bash"]).ToArgs();

        // Replacing the preamble here was measured at ~2.5x the cost: it invalidates the
        // shared prefix and forces a cache write instead of a cache read.
        Assert.DoesNotContain("--system-prompt", args);
        Assert.Contains("--exclude-dynamic-system-prompt-sections", args);
        Assert.Equal("Read,Edit,Bash", ArgValue(args, "--tools"));
        Assert.Equal("bypassPermissions", ArgValue(args, "--permission-mode"));
    }

    [Fact]
    public void Thick_never_carries_a_replacement_system_prompt()
    {
        Assert.Null(AgentProfile.Thick(ModelTier.Sonnet, ["Read"]).SystemPrompt);
    }

    [Fact]
    public void Structured_output_raises_the_turn_limit_above_one()
    {
        // A structured call spends one turn answering and another emitting against the
        // schema, so max-turns 1 can only ever terminate as error_max_turns.
        var profile = AgentProfile.Thin(ModelTier.Sonnet, "prompt", maxTurns: 1);

        Assert.Equal("1", ArgValue(profile.ToArgs(structuredOutput: false), "--max-turns"));
        Assert.Equal(
            AgentProfile.StructuredOutputTurnFloor.ToString(),
            ArgValue(profile.ToArgs(structuredOutput: true), "--max-turns"));
    }

    [Fact]
    public void Transport_applies_the_turn_floor_whenever_a_schema_is_attached()
    {
        var request = new AgentRequest
        {
            Prompt = "classify",
            Profile = AgentProfile.Thin(ModelTier.Haiku, "sys", maxTurns: 1),
            JsonSchema = "{\"type\":\"object\"}"
        };

        var args = CliAgentTransport.BuildArgs(request);
        Assert.Equal(AgentProfile.StructuredOutputTurnFloor.ToString(), ArgValue(args, "--max-turns"));
        Assert.Contains("--json-schema", args);
    }

    [Fact]
    public void Budget_ceiling_is_passed_to_the_transport()
    {
        var request = new AgentRequest
        {
            Prompt = "work",
            Profile = AgentProfile.Thick(ModelTier.Sonnet, ["Read"]),
            MaxBudgetUsd = 1.25m
        };

        Assert.Equal("1.25", ArgValue(CliAgentTransport.BuildArgs(request), "--max-budget-usd"));
    }
}

public class ResultParsingTests
{
    private static AgentRunResult Parse(string json) =>
        CliAgentTransport.FromResultEvent(
            AgentEvent.TryParse(json)!, sessionId: "s", assistantText: "", toolsUsed: [], durationMs: 1);

    [Fact]
    public void A_clean_result_is_a_success_with_usage_and_cost()
    {
        var run = Parse(
            """
            {"type":"result","subtype":"success","is_error":false,"result":"done",
             "total_cost_usd":0.0125,"num_turns":3,"stop_reason":"end_turn",
             "usage":{"input_tokens":10,"output_tokens":40,"cache_read_input_tokens":900,"cache_creation_input_tokens":100}}
            """);

        Assert.True(run.Success);
        Assert.Null(run.Error);
        Assert.Null(run.RawResult);
        Assert.Equal(0.0125m, run.CostUsd);
        Assert.Equal(1050, run.Usage.Total);
    }

    [Fact]
    public void An_abnormal_end_is_described_rather_than_echoed_as_success()
    {
        // Observed in a real run: the transport reported is_error with the subtype still
        // reading "success", which naive handling surfaced as "gate failed: success".
        var run = Parse(
            """
            {"type":"result","subtype":"success","is_error":true,"result":"",
             "total_cost_usd":0.25,"num_turns":16,"stop_reason":"stop_sequence"}
            """);

        Assert.False(run.Success);
        Assert.NotEqual("success", run.Error);
        Assert.Contains("stop_sequence", run.Error);
        Assert.Contains("16 turn", run.Error);
    }

    [Fact]
    public void A_failure_keeps_the_raw_message_for_diagnosis()
    {
        var run = Parse("""{"type":"result","subtype":"error_max_turns","is_error":true,"num_turns":1}""");

        Assert.False(run.Success);
        Assert.Equal("error_max_turns", run.Error);
        Assert.Contains("error_max_turns", run.RawResult);
    }

    [Fact]
    public void An_api_error_status_wins_over_the_subtype()
    {
        var run = Parse(
            """{"type":"result","subtype":"error","is_error":true,"api_error_status":"rate_limit_error","num_turns":0}""");

        Assert.Equal("rate_limit_error", run.Error);
    }
}

public class StructuredOutputTests
{
    [Theory]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    [InlineData("```json\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("```\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("Here you go:\n{\"a\":1}", "{\"a\":1}")]
    public void Extracts_json_however_the_model_wrapped_it(string input, string expected) =>
        Assert.Equal(expected, AgentRunResult.ExtractJson(input));

    [Fact]
    public void Returns_null_when_there_is_no_json()
        => Assert.Null(AgentRunResult.ExtractJson("no json at all"));

    private sealed record Shape(string Name, int Count);

    [Fact]
    public void Deserialises_structured_payloads()
    {
        var result = FakeTransport.Success("```json\n{\"name\":\"x\",\"count\":3}\n```");
        Assert.True(result.TryStructured<Shape>(out var shape, out _));
        Assert.Equal("x", shape.Name);
        Assert.Equal(3, shape.Count);
    }
}

public class ResponseCacheTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    private static AgentRequest Request(string prompt, string? digest = null) => new()
    {
        Prompt = prompt,
        Profile = AgentProfile.Thin(ModelTier.Haiku, "sys"),
        ContextDigest = digest
    };

    [Fact]
    public void Hit_returns_the_stored_result_without_a_model_call()
    {
        var cache = new ResponseCache(_dir);
        var request = Request("same question");

        Assert.False(cache.TryGet(request, out _));
        cache.Put(request, FakeTransport.Success("answer"));

        Assert.True(cache.TryGet(request, out var hit));
        Assert.Equal("answer", hit.Text);
        Assert.True(hit.CacheHit);
    }

    [Fact]
    public void Same_words_about_a_different_world_is_a_miss()
    {
        var cache = new ResponseCache(_dir);
        cache.Put(Request("q", digest: "repo-v1"), FakeTransport.Success("answer"));

        Assert.False(cache.TryGet(Request("q", digest: "repo-v2"), out _));
    }

    [Fact]
    public void Failures_are_not_cached()
    {
        var cache = new ResponseCache(_dir);
        var request = Request("q");

        cache.Put(request, AgentRunResult.Failure("boom"));
        Assert.False(cache.TryGet(request, out _));
    }

    [Fact]
    public void NoCache_requests_bypass_the_cache_entirely()
    {
        var cache = new ResponseCache(_dir);
        var request = Request("q");
        cache.Put(request, FakeTransport.Success("answer"));

        Assert.False(cache.TryGet(request with { NoCache = true }, out _));
    }
}

public class AgentRunnerTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    private static AgentRequest Request(string station = "plan") => new()
    {
        Prompt = "do the thing",
        Profile = AgentProfile.Thin(ModelTier.Haiku, "sys", station)
    };

    [Fact]
    public async Task Second_identical_call_is_served_from_cache()
    {
        var transport = new FakeTransport().Respond("plan", "result");
        var runner = new AgentRunner(transport, new ResponseCache(_dir));

        await runner.RunAsync(Request());
        var second = await runner.RunAsync(Request());

        Assert.Equal(1, transport.Calls);
        Assert.True(second.CacheHit);
        Assert.Equal(1, runner.CacheHits);
    }

    [Fact]
    public async Task Transient_failures_are_retried_and_permanent_ones_are_not()
    {
        var transient = new FakeTransport().Fail("plan", "connection reset by peer");
        var runner = new AgentRunner(transient, cache: null,
            new AgentRunnerOptions { MaxRetries = 2, BaseBackoff = TimeSpan.Zero });

        await runner.RunAsync(Request());
        Assert.Equal(3, transient.Calls);   // initial attempt plus two retries

        var permanent = new FakeTransport().Fail("plan", "invalid schema");
        var strict = new AgentRunner(permanent, cache: null,
            new AgentRunnerOptions { MaxRetries = 2, BaseBackoff = TimeSpan.Zero });

        await strict.RunAsync(Request());
        Assert.Equal(1, permanent.Calls);
    }

    [Fact]
    public async Task A_rate_limited_failure_cools_down_before_retrying_rather_than_stalling()
    {
        // An inferred limit is a guess, so its cooldown must stay short. Treating it like a
        // measured window made one transient rate-limit error stall the factory for minutes.
        var governor = new UsageGovernor(new UsagePolicy { InferredCooldown = TimeSpan.Zero });
        var transport = new FakeTransport().Fail("plan", "rate_limit_error");

        var runner = new AgentRunner(transport, cache: null,
            new AgentRunnerOptions { MaxRetries = 1, BaseBackoff = TimeSpan.Zero }, governor);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await runner.RunAsync(Request());
        stopwatch.Stop();

        Assert.Equal(2, transport.Calls);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"retry took {stopwatch.Elapsed.TotalSeconds:F1}s; an inferred limit must not gate it");

        // The default cooldown is short enough that a retry is paced, not parked.
        Assert.True(UsagePolicy.Default.InferredCooldown <= TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Spend_and_usage_accumulate_for_reporting()
    {
        var transport = new FakeTransport().Respond("plan", _ => FakeTransport.Success("ok", cost: 0.05m));
        var runner = new AgentRunner(transport, cache: null);

        await runner.RunAsync(Request());
        await runner.RunAsync(Request());

        Assert.Equal(0.10m, runner.TotalCostUsd);
        Assert.Equal(360, runner.TotalUsage.Total);   // 2 x (120 in + 60 out)
    }
}

public class TokenEconomyTests
{
    [Fact]
    public void Thin_profile_reduction_matches_the_measurement()
    {
        Assert.Equal(19_336, TokenEconomy.NaiveBaselineInputTokens);
        Assert.Equal(165, TokenEconomy.ThinProfileInputTokens);
        Assert.Equal(19_171, TokenEconomy.AmbientOverheadTokens);
        Assert.InRange(TokenEconomy.ThinReductionRatio, 0.99, 0.9915);
    }

    [Fact]
    public void Only_thin_runs_claim_overhead_savings()
    {
        RunRecord Run(TokenProfile profile) => new()
        {
            RunId = "r", ItemId = "i", StationId = "s", Profile = profile,
            Usage = new TokenUsage(100, 50, 0, 0)
        };

        var runs = new[] { Run(TokenProfile.Thin), Run(TokenProfile.Thick), Run(TokenProfile.Thin) };

        // Thick stations keep the preamble deliberately, so they never avoided the overhead.
        Assert.Equal(2L * TokenEconomy.AmbientOverheadTokens, TokenEconomy.OverheadAvoided(runs));
    }

    [Fact]
    public void Cache_hits_avoid_the_whole_call_not_just_the_overhead()
    {
        var runs = new[]
        {
            new RunRecord { RunId = "a", ItemId = "i", StationId = "s", CacheHit = true,
                Usage = new TokenUsage(1000, 500, 0, 0) }
        };

        Assert.Equal(1500, TokenEconomy.CacheAvoided(runs));
        Assert.Equal(0, TokenEconomy.OverheadAvoided(runs));   // no call was made to strip
    }
}
