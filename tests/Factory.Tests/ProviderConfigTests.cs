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
}
