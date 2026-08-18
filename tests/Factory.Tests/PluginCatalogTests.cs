using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

public class PluginCatalogTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    private string PluginsDirWithFixture()
    {
        var plugins = Path.Combine(_dir, "plugins");
        PluginFixture.InstallWithContractAssemblyInto(plugins);
        return plugins;
    }

    [Fact]
    public void Resolves_a_built_in_without_touching_the_plugins_directory()
    {
        var registry = new ProviderRegistry();
        registry.Register<IRunHistorySink>("noop", _ => new NoopSink());

        Assert.IsType<NoopSink>(registry.Resolve<IRunHistorySink>(new ProviderRef("noop")));
    }

    [Fact]
    public void Loads_a_provider_from_a_plugin_assembly()
    {
        var registry = new ProviderRegistry();
        PluginCatalog.LoadInto(registry, PluginsDirWithFixture(), _ => { });

        var sink = registry.Resolve<IRunHistorySink>(new ProviderRef("counting"));

        Assert.Equal("CountingSink", sink.GetType().Name);
    }

    [Fact]
    public void A_plugin_type_unifies_with_the_host_contract_type()
    {
        var registry = new ProviderRegistry();
        PluginCatalog.LoadInto(registry, PluginsDirWithFixture(), _ => { });

        var sink = registry.Resolve<IRunHistorySink>(new ProviderRef("counting"));

        var contractOnThePlugin = sink.GetType().GetInterfaces().Single(i => i.Name == nameof(IRunHistorySink));

        Assert.Same(typeof(IRunHistorySink), contractOnThePlugin);
    }

    [Fact]
    public void An_unknown_provider_name_names_what_is_available()
    {
        var registry = new ProviderRegistry();
        registry.Register<IRunHistorySink>("noop", _ => new NoopSink());

        var ex = Assert.Throws<InvalidOperationException>(
            () => registry.Resolve<IRunHistorySink>(new ProviderRef("missing")));

        Assert.Contains("missing", ex.Message);
        Assert.Contains("noop", ex.Message);
    }

    [Fact]
    public void A_missing_plugins_directory_is_not_an_error()
    {
        var registry = new ProviderRegistry();
        PluginCatalog.LoadInto(registry, Path.Combine(_dir, "absent"), _ => { });
    }

    [Fact]
    public void A_built_in_registered_before_the_scan_shadows_the_plugin_provider()
    {
        var log = new List<string>();
        var registry = new ProviderRegistry(log.Add);
        registry.Register<IRunHistorySink>("counting", _ => new NoopSink());

        PluginCatalog.LoadInto(registry, PluginsDirWithFixture(), log.Add);

        Assert.IsType<NoopSink>(registry.Resolve<IRunHistorySink>(new ProviderRef("counting")));
        Assert.Contains(log, line => line.Contains("counting") && line.Contains(nameof(IRunHistorySink))
                                     && line.Contains("shadowed by a built-in"));
    }

    [Fact]
    public void A_built_in_registered_after_the_scan_displaces_the_plugin_provider_by_name()
    {
        var log = new List<string>();
        var registry = new ProviderRegistry(log.Add);

        PluginCatalog.LoadInto(registry, PluginsDirWithFixture(), log.Add);
        registry.Register<IRunHistorySink>("counting", _ => new NoopSink());

        Assert.IsType<NoopSink>(registry.Resolve<IRunHistorySink>(new ProviderRef("counting")));
        Assert.Contains(log, line => line.Contains("counting") && line.Contains(nameof(IRunHistorySink))
                                     && line.Contains("displaced by the built-in"));
    }

    [Fact]
    public void A_provider_with_no_usable_constructor_is_skipped_without_stopping_the_scan()
    {
        var registry = new ProviderRegistry();
        var log = new List<string>();

        PluginCatalog.LoadInto(registry, PluginsDirWithFixture(), log.Add);

        Assert.Equal("CountingSink", registry.Resolve<IRunHistorySink>(new ProviderRef("counting")).GetType().Name);
        Assert.Contains(log, line => line.Contains("unconstructable") && line.Contains("UnconstructableSink"));
    }

    [Fact]
    public void A_provider_implementing_two_ports_is_registered_under_both()
    {
        var registry = new ProviderRegistry();
        PluginCatalog.LoadInto(registry, PluginsDirWithFixture(), _ => { });

        var asSink = registry.Resolve<IRunHistorySink>(new ProviderRef("dual-port"));
        var asHistory = registry.Resolve<IRunHistory>(new ProviderRef("dual-port"));

        Assert.Equal("DualPortStore", asSink.GetType().Name);
        Assert.Equal("DualPortStore", asHistory.GetType().Name);
    }

    [Fact]
    public void A_provider_receives_its_options_and_runs_when_called_through_the_contract()
    {
        var recorded = Path.Combine(_dir, "recorded.txt");
        var registry = new ProviderRegistry();
        PluginCatalog.LoadInto(registry, PluginsDirWithFixture(), _ => { });

        var sink = registry.Resolve<IRunHistorySink>(
            new ProviderRef("recording", new Dictionary<string, string> { ["path"] = recorded }));
        sink.Emit(new FactoryNote("hello"));

        Assert.Equal($"{nameof(FactoryNote)}{Environment.NewLine}", File.ReadAllText(recorded));
    }

    [Fact]
    public void A_broken_assembly_is_reported_and_does_not_stop_the_scan()
    {
        var plugins = PluginsDirWithFixture();

        // Named to sort first: the scan is ordered, so a broken file discovered last would prove
        // nothing about whether the scan continues past it.
        File.WriteAllText(Path.Combine(plugins, "AAA-broken.dll"), "not an assembly");
        var registry = new ProviderRegistry();
        var log = new List<string>();

        PluginCatalog.LoadInto(registry, plugins, log.Add);

        Assert.Equal("CountingSink", registry.Resolve<IRunHistorySink>(new ProviderRef("counting")).GetType().Name);
        Assert.Contains(log, line => line.Contains("AAA-broken.dll") && line.Contains("could not be loaded"));
    }

    [Fact]
    public void An_assembly_that_yields_no_providers_is_reported()
    {
        var registry = new ProviderRegistry();
        var log = new List<string>();

        PluginCatalog.LoadInto(registry, PluginsDirWithFixture(), log.Add);

        Assert.Contains(log, line => line.Contains("Factory.Core.dll") && line.Contains("registered no providers"));
    }

    [Fact]
    public void A_provider_built_against_another_contract_version_is_refused_and_named()
    {
        var registry = new ProviderRegistry();
        var log = new List<string>();

        PluginCatalog.LoadInto(registry, PluginsDirWithFixture(), log.Add);

        Assert.Throws<InvalidOperationException>(
            () => registry.Resolve<IRunHistorySink>(new ProviderRef("future")));
        Assert.Contains(log, line => line.Contains("future") && line.Contains("v2") && line.Contains("v1"));
    }

    [Fact]
    public void Scanning_the_same_directory_twice_reuses_one_load_context_per_assembly()
    {
        var plugins = PluginsDirWithFixture();
        var first = new ProviderRegistry();
        var second = new ProviderRegistry();

        PluginCatalog.LoadInto(first, plugins, _ => { });
        PluginCatalog.LoadInto(second, plugins, _ => { });

        // A second context would load a second copy of the assembly, so the same plugin class
        // would resolve as two distinct types.
        Assert.Same(
            first.Resolve<IRunHistorySink>(new ProviderRef("counting")).GetType(),
            second.Resolve<IRunHistorySink>(new ProviderRef("counting")).GetType());
    }

    [Fact]
    public void Host_falls_back_to_built_ins_when_no_plugins_are_present()
    {
        var dir = TempDir.Create();
        try
        {
            using var host = FactoryHost.Init(dir, transport: new FakeTransport());

            Assert.IsType<GuardedWorkItemStore>(host.Services.Items);
            Assert.IsType<FanOutRunHistory>(host.Services.History);
        }
        finally { TempDir.Delete(dir); }
    }

    private sealed class NoopSink : IRunHistorySink
    {
        public void Emit(FactoryEvent evt) { }
        public void Flush() { }
    }
}
