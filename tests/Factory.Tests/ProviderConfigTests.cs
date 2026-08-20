using Factory.Core;

namespace Factory.Tests;

public class ProviderConfigTests
{
    [Fact]
    public void DefaultsSelectTheBuiltInProviders()
    {
        var config = new FactoryConfig { Name = "demo" };

        Assert.Equal("ledger", config.WorkItemStore.Provider);
        Assert.Equal("jsonl", config.RunHistory.Writer);
        Assert.Empty(config.RunHistory.Sinks);
    }

    [Fact]
    public void RoundTripsASinkWithOptions()
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
    public void AProviderNamedWithNoOptionsStillHasAnEmptyOptionSet()
    {
        // What the spec's own config example writes, and what `factory init` leaves behind.
        var restored = FactoryJson.Read<ProviderRef>("""{"provider":"beads"}""")!;

        Assert.Equal("beads", restored.Provider);
        Assert.Empty(restored.Options);
        Assert.Equal("wi", restored.Options.GetValueOrDefault("prefix", "wi"));
    }
}
