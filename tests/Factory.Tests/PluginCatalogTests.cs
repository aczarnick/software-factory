using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

public class PluginCatalogTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    /// <summary>Copies the built fixture plugin into a temp plugins directory, alongside a
    /// copy of <c>Factory.Core</c> — as a third-party plugin that packaged the contract
    /// assembly would ship. That sibling copy is what the load context has to refuse.</summary>
    private string PluginsDirWithFixture()
    {
        var plugins = Path.Combine(_dir, "plugins");
        Directory.CreateDirectory(plugins);

        // The test binary runs from bin/<configuration>/<framework>; the fixture builds to its own.
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var source = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "Factory.TestPlugin",
            "bin", output.Parent!.Name, output.Name, "Factory.TestPlugin.dll"));

        if (!File.Exists(source))
            throw new InvalidOperationException(
                $"Fixture plugin not built: {source}. Run 'dotnet build' at the solution root first.");

        File.Copy(source, Path.Combine(plugins, "Factory.TestPlugin.dll"));
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Factory.Core.dll"),
            Path.Combine(plugins, "Factory.Core.dll"));

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
    public void A_built_in_of_the_same_name_shadows_the_plugin_provider()
    {
        var registry = new ProviderRegistry();
        registry.Register<IRunHistorySink>("counting", _ => new NoopSink());
        var log = new List<string>();

        PluginCatalog.LoadInto(registry, PluginsDirWithFixture(), log.Add);

        Assert.IsType<NoopSink>(registry.Resolve<IRunHistorySink>(new ProviderRef("counting")));
        Assert.Contains(log, line => line.Contains("counting") && line.Contains("shadowed by a built-in"));
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

    private sealed class NoopSink : IRunHistorySink
    {
        public void Emit(FactoryEvent evt) { }
        public void Flush() { }
    }
}
