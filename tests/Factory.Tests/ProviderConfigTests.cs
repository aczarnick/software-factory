using Factory.Core;

namespace Factory.Tests;

public class ProviderConfigTests
{
    [Fact]
    public void Defaults_select_the_built_in_providers()
    {
        var config = new FactoryConfig { Name = "demo" };

        Assert.Equal("ledger", config.WorkItemStore.Provider);
        Assert.Equal("jsonl", config.RunHistory.Writer);
        Assert.Empty(config.RunHistory.Sinks);
    }

    [Fact]
    public void Round_trips_a_sink_with_options()
    {
        var config = new FactoryConfig
        {
            Name = "demo",
            RunHistory = new RunHistoryConfig
            {
                Writer = "jsonl",
                Sinks = [new ProviderRef("tracer", new Dictionary<string, string> { ["url"] = "x" })]
            }
        };

        var restored = FactoryJson.Read<FactoryConfig>(FactoryJson.Write(config))!;

        Assert.Equal("tracer", restored.RunHistory.Sinks[0].Provider);
        Assert.Equal("x", restored.RunHistory.Sinks[0].Options["url"]);
    }

    [Fact]
    public void A_provider_named_with_no_options_still_has_an_empty_option_set()
    {
        // What the spec's own config example writes, and what `factory init` leaves behind.
        var restored = FactoryJson.Read<ProviderRef>("""{"provider":"beads"}""")!;

        Assert.Equal("beads", restored.Provider);
        Assert.Empty(restored.Options);
        Assert.Equal("wi", restored.Options.GetValueOrDefault("prefix", "wi"));
    }
}
