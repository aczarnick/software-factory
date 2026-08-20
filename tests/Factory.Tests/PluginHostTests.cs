using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

/// <summary>The join D5 rests on: a provider named in <c>factory.json</c>, loaded from
/// <c>.factory/plugins</c>, composed into a live host.</summary>
public sealed class PluginHostTests : IDisposable
{
    private readonly string _repo = TempDir.Create();
    public void Dispose() => TempDir.Delete(_repo);

    private void DeployWithPluginInstalled()
    {
        using (FactoryHost.Init(_repo, transport: new FakeTransport())) { }
        PluginFixture.InstallInto(new FactoryPaths(_repo).PluginsDir);
    }

    private void Reconfigure(Func<FactoryConfig, FactoryConfig> change)
    {
        var paths = new FactoryPaths(_repo);
        var config = FactoryJson.Read<FactoryConfig>(File.ReadAllText(paths.Config))!;
        File.WriteAllText(paths.Config, FactoryJson.Write(change(config), pretty: true));
    }

    [Fact]
    public void ASinkNamedInConfigReceivesEventsRecordedThroughTheHost()
    {
        var recorded = Path.Combine(_repo, "sink.log");
        DeployWithPluginInstalled();
        Reconfigure(config => config with
        {
            RunHistory = new RunHistoryConfig
            {
                Writer = "jsonl",
                Sinks = [new ProviderRef("recording", new Dictionary<string, string> { ["path"] = recorded })]
            }
        });

        using (var host = FactoryHost.Open(_repo, transport: new FakeTransport()))
            host.Services.Record(new FactoryNote("through the host"));

        Assert.Equal($"{nameof(FactoryNote)}{Environment.NewLine}", File.ReadAllText(recorded));
    }

    [Fact]
    public void APluginTargetingAnotherContractVersionDoesNotStopTheFactoryOpening()
    {
        DeployWithPluginInstalled();
        var log = new List<string>();

        using var host = FactoryHost.Open(_repo, log.Add, transport: new FakeTransport());
        host.Services.Record(new FactoryNote("started anyway"));

        Assert.Contains(log, line => line.Contains("future")
                                     && line.Contains($"v{FactoryVersion.ContractVersion + 1}")
                                     && line.Contains($"v{FactoryVersion.ContractVersion}"));
        Assert.Single(host.Services.History.ReadFrom(0));
    }

    [Fact]
    public void ASinkThatFailsToConstructIsDroppedAndTheHostKeepsRecording()
    {
        DeployWithPluginInstalled();
        Reconfigure(config => config with
        {
            RunHistory = new RunHistoryConfig { Writer = "jsonl", Sinks = [new ProviderRef("exploding")] }
        });
        var log = new List<string>();

        using var host = FactoryHost.Open(_repo, log.Add, transport: new FakeTransport());
        host.Services.Record(new FactoryNote("still recording"));

        Assert.Contains(log, line => line.Contains("exploding")
                                     && line.Contains("could not be created")
                                     && line.Contains("cannot reach the tracing backend"));
        Assert.Single(host.Services.History.ReadFrom(0));
    }

    [Fact]
    public void AStoreThatFailsToConstructHaltsTheHostWithANamedStoreException()
    {
        DeployWithPluginInstalled();
        Reconfigure(config => config with { WorkItemStore = new ProviderRef("exploding-store") });

        var ex = Assert.Throws<WorkItemStoreException>(
            () => FactoryHost.Open(_repo, transport: new FakeTransport()));

        Assert.Equal("exploding-store", ex.Provider);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("cannot reach the backlog", ex.Message);
    }
}
