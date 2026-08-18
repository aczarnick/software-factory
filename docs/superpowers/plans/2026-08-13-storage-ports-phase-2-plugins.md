# Plugin Infrastructure (Phase 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Load third-party `IWorkItemStore` and `IRunHistorySink` implementations from `.factory/plugins/*.dll`, selected by name in config, with failure boundaries that match each port's risk.

**Architecture:** A `ProviderRegistry` maps names to factories; built-ins are pre-seeded so a built-in and a plugin are selected identically. `PluginCatalog` scans the plugins directory, loads each assembly in its own `PluginLoadContext`, and registers types marked `[FactoryProvider]`. Two guard decorators encode the asymmetry from the spec: a failing work-item store halts the factory, a failing sink degrades and is disabled.

**Tech Stack:** .NET 10, `System.Runtime.Loader.AssemblyLoadContext`, `AssemblyDependencyResolver`, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-13-storage-adapters-design.md`

**Depends on:** Phase 1 (`docs/superpowers/plans/2026-08-13-storage-ports-phase-1.md`) must be complete and merged.

## Carried Forward From Phase 1

Found during phase 1's final whole-branch review; read before Task 4 (plugin load context and
catalog) and Task 5 (wire provider selection into `FactoryHost`).

- **Providers cannot be constructed uniformly.** `LedgerWorkItemStore`'s constructor is
  `(IRunHistory, FactoryState)`, and a beads-backed store's will be something else entirely.
  `ProviderRegistry` therefore needs a per-provider factory or a shared context object — a
  uniform `Activator.CreateInstance` over discovered types will not work.

- **`FactoryHost.Open` calls `history.Replay()`, which is not on the port.** Settled — see the
  ruling in Task 5 Step 3: use `FactoryState.Replay(history.ReadFrom(0))` and do not add
  `Replay()` to `IRunHistory`.

- **`IRunHistory.ReadFrom` returns a lazy iterator that holds a file handle** for the life of
  the enumeration. Every phase-1 consumer enumerates to completion in a single expression, so
  nothing leaks today. A plugin author implementing the port should be told this explicitly.

## Global Constraints

- .NET 10 SDK pinned to `10.0.400` by `global.json`. Do not change the pin.
- `Factory.Core` must remain **dependency-free**. It is the plugin ABI.
- One top-level type per file, named after the type.
- XML doc `<summary>` on public APIs only.
- Retry a failed build once before believing it (`csc.dll exited with code 132`).
- Verification gate: `dotnet build` and `dotnet test` both green, output shown.
- **No beads in this phase.** The only providers that exist are the built-ins from phase 1 and the test fixture plugin.

---

### Task 1: Provider attribute and contract version

**Files:**
- Create: `src/Factory.Core/FactoryProviderAttribute.cs`
- Modify: `src/Factory.Core/FactoryVersion.cs` — add `ContractVersion`
- Test: `tests/Factory.Tests/PluginCatalogTests.cs` (created in Task 4; nothing to test yet)

**Interfaces:**
- Produces: `FactoryProviderAttribute(string name)` with `int Contract { get; init; }`, and `FactoryVersion.ContractVersion`.

- [ ] **Step 1: Add the contract version constant**

In `src/Factory.Core/FactoryVersion.cs`, add:

```csharp
    /// <summary>Major version of the plugin ABI. Bump only on a breaking change to
    /// <see cref="IWorkItemStore"/>, <see cref="IRunHistory"/>, <see cref="IRunHistorySink"/>,
    /// or any type they expose. A plugin built against a different major is refused at load.</summary>
    public const int ContractVersion = 1;
```

- [ ] **Step 2: Create the attribute**

```csharp
namespace Factory.Core;

/// <summary>Marks a type as a storage provider discoverable by name. Applied by plugin
/// authors; the factory's own built-ins are registered directly.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class FactoryProviderAttribute(string name) : Attribute
{
    /// <summary>Name used to select this provider in <c>factory.json</c>.</summary>
    public string Name { get; } = name;

    /// <summary>Plugin ABI major this provider was built against. A mismatch with
    /// <see cref="FactoryVersion.ContractVersion"/> is refused at load with a named error.</summary>
    public int Contract { get; init; } = 1;
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Factory.Core/Factory.Core.csproj`
Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Factory.Core/FactoryProviderAttribute.cs src/Factory.Core/FactoryVersion.cs
git commit -m "Add the plugin provider attribute and contract version"
```

---

### Task 2: Provider configuration

**Files:**
- Create: `src/Factory.Core/ProviderRef.cs`
- Create: `src/Factory.Core/RunHistoryConfig.cs`
- Modify: `src/Factory.Core/FactoryPaths.cs` — add `PluginsDir`, create it in `EnsureCreated`, and add two properties to `FactoryConfig`
- Test: `tests/Factory.Tests/ProviderConfigTests.cs`

**Interfaces:**
- Produces: `ProviderRef(string Provider, Dictionary<string, string> Options)`, `RunHistoryConfig`, `FactoryConfig.WorkItemStore`, `FactoryConfig.RunHistory`, `FactoryPaths.PluginsDir`.

`FactoryConfig` currently lives in `FactoryPaths.cs`, which already breaks the one-type-per-file rule. Do **not** fix that here — it is unrelated churn. Add the two properties in place and leave the split for a dedicated change.

- [ ] **Step 1: Write the failing test**

Create `tests/Factory.Tests/ProviderConfigTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~ProviderConfigTests`
Expected: FAIL — `ProviderRef` does not exist.

- [ ] **Step 3: Create `ProviderRef`**

```csharp
namespace Factory.Core;

/// <summary>Selects a provider by name, with provider-specific options.</summary>
public sealed record ProviderRef(string Provider, Dictionary<string, string> Options)
{
    public ProviderRef(string provider) : this(provider, []) { }
}
```

- [ ] **Step 4: Create `RunHistoryConfig`**

```csharp
namespace Factory.Core;

/// <summary>The durable local writer, plus any additional best-effort sinks. The writer is
/// always present: it is the copy the prompt promotion gate mines.</summary>
public sealed record RunHistoryConfig
{
    public string Writer { get; init; } = "jsonl";
    public IReadOnlyList<ProviderRef> Sinks { get; init; } = [];
}
```

- [ ] **Step 5: Extend `FactoryConfig` and `FactoryPaths`**

In `src/Factory.Core/FactoryPaths.cs`, add to `FactoryConfig`:

```csharp
    /// <summary>Backlog provider. Exactly one is active.</summary>
    public ProviderRef WorkItemStore { get; init; } = new("ledger");

    public RunHistoryConfig RunHistory { get; init; } = new();
```

Add to `FactoryPaths`:

```csharp
    /// <summary>Third-party provider assemblies, loaded at open.</summary>
    public string PluginsDir => Path.Combine(Root, "plugins");
```

And add to `EnsureCreated`:

```csharp
        Directory.CreateDirectory(PluginsDir);
```

- [ ] **Step 6: Run it to verify it passes**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~ProviderConfigTests`
Expected: PASS, 2 tests.

- [ ] **Step 7: Commit**

```bash
git add src/Factory.Core/ProviderRef.cs src/Factory.Core/RunHistoryConfig.cs \
        src/Factory.Core/FactoryPaths.cs tests/Factory.Tests/ProviderConfigTests.cs
git commit -m "Add provider selection to factory configuration"
```

---

### Task 3: Guard decorators

**Files:**
- Create: `src/Factory.Core/WorkItemStoreException.cs`
- Create: `src/Factory.Runtime/Providers/GuardedWorkItemStore.cs`
- Create: `src/Factory.Runtime/Providers/GuardedRunHistorySink.cs`
- Create: `src/Factory.Runtime/Providers/FanOutRunHistory.cs`
- Test: `tests/Factory.Tests/GuardedProviderTests.cs`

**Interfaces:**
- Consumes: `IWorkItemStore`, `IRunHistory`, `IRunHistorySink` (phase 1).
- Produces: `WorkItemStoreException`, `GuardedWorkItemStore(IWorkItemStore inner, string providerName)`, `GuardedRunHistorySink(IRunHistorySink inner, string providerName, int maxFailures, Action<string> log)`, `FanOutRunHistory(IRunHistory writer, IReadOnlyList<IRunHistorySink> sinks)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Factory.Tests/GuardedProviderTests.cs`:

```csharp
using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

public class GuardedProviderTests
{
    private sealed class ThrowingSink : IRunHistorySink
    {
        public int EmitCount;
        public void Emit(FactoryEvent evt) { EmitCount++; throw new InvalidOperationException("sink down"); }
        public void Flush() { }
    }

    private sealed class ThrowingStore : IWorkItemStore
    {
        public WorkItem Add(WorkItem item) => throw new InvalidOperationException("store down");
        public WorkItem Update(WorkItem item) => throw new InvalidOperationException("store down");
        public WorkItem Transition(WorkItem item, WorkItemState to, string? reason) => throw new InvalidOperationException("store down");
        public WorkItem? Get(string id) => throw new InvalidOperationException("store down");
        public IReadOnlyList<WorkItem> All() => throw new InvalidOperationException("store down");
        public WorkItem? TryClaim(string owner) => throw new InvalidOperationException("store down");
        public void Heartbeat(string id) => throw new InvalidOperationException("store down");
        public void Release(string id, string reason) => throw new InvalidOperationException("store down");
        public void Sync() => throw new InvalidOperationException("store down");
        public IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan) => throw new InvalidOperationException("store down");
    }

    [Fact]
    public void A_failing_store_halts_with_a_named_exception()
    {
        var store = new GuardedWorkItemStore(new ThrowingStore(), "beads");

        var ex = Assert.Throws<WorkItemStoreException>(() => store.All());

        Assert.Contains("beads", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void A_failing_sink_is_disabled_after_the_failure_ceiling()
    {
        var inner = new ThrowingSink();
        var warnings = new List<string>();
        var sink = new GuardedRunHistorySink(inner, "tracer", maxFailures: 2, warnings.Add);

        for (var i = 0; i < 5; i++) sink.Emit(new FactoryNote("x"));

        Assert.Equal(2, inner.EmitCount);
        Assert.Contains(warnings, w => w.Contains("disabled"));
    }

    [Fact]
    public void Fan_out_still_writes_durably_when_every_sink_fails()
    {
        var dir = TempDir.Create();
        try
        {
            using var writer = new JsonlRunHistory(Path.Combine(dir, "ledger.jsonl"));
            var sink = new GuardedRunHistorySink(new ThrowingSink(), "tracer", 1, _ => { });
            var history = new FanOutRunHistory(writer, [sink]);

            history.Append(new FactoryNote("survives"));

            Assert.Single(history.ReadFrom(0));
        }
        finally { TempDir.Delete(dir); }
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~GuardedProviderTests`
Expected: FAIL — the guard types do not exist.

- [ ] **Step 3: Create `WorkItemStoreException`**

```csharp
namespace Factory.Core;

/// <summary>A backlog provider failed. Never swallowed: the backlog has a single authority,
/// so continuing on a store that cannot answer would risk working the wrong queue.</summary>
public sealed class WorkItemStoreException(string provider, string operation, Exception inner)
    : Exception($"Work item store '{provider}' failed during {operation}: {inner.Message}", inner)
{
    public string Provider { get; } = provider;
    public string Operation { get; } = operation;
}
```

- [ ] **Step 4: Create `GuardedWorkItemStore`**

```csharp
using Factory.Core;

namespace Factory.Runtime;

/// <summary>Translates any provider failure into <see cref="WorkItemStoreException"/> so a
/// broken backlog stops the factory loudly instead of silently returning an empty queue.</summary>
public sealed class GuardedWorkItemStore(IWorkItemStore inner, string providerName) : IWorkItemStore
{
    public WorkItem Add(WorkItem item) => Guard(nameof(Add), () => inner.Add(item));
    public WorkItem Update(WorkItem item) => Guard(nameof(Update), () => inner.Update(item));

    public WorkItem Transition(WorkItem item, WorkItemState to, string? reason) =>
        Guard(nameof(Transition), () => inner.Transition(item, to, reason));

    public WorkItem? Get(string id) => Guard(nameof(Get), () => inner.Get(id));
    public IReadOnlyList<WorkItem> All() => Guard(nameof(All), inner.All);
    public WorkItem? TryClaim(string owner) => Guard(nameof(TryClaim), () => inner.TryClaim(owner));
    public void Heartbeat(string id) => Guard(nameof(Heartbeat), () => { inner.Heartbeat(id); return 0; });
    public void Release(string id, string reason) => Guard(nameof(Release), () => { inner.Release(id, reason); return 0; });
    public void Sync() => Guard(nameof(Sync), () => { inner.Sync(); return 0; });

    public IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan) =>
        Guard(nameof(Reclaim), () => inner.Reclaim(olderThan));

    private T Guard<T>(string operation, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is not WorkItemStoreException)
        {
            throw new WorkItemStoreException(providerName, operation, ex);
        }
    }
}
```

`InvalidOperationException` from an illegal state transition is *not* a provider failure, but it is also not distinguishable here without leaking policy into the decorator. Accept the wrapping: the inner exception is preserved and the message names the operation.

- [ ] **Step 5: Create `GuardedRunHistorySink`**

```csharp
using Factory.Core;

namespace Factory.Runtime;

/// <summary>Contains sink failures. The durable writer already holds the record, so a sink
/// that cannot be reached is a warning, not an outage — and one that keeps failing is
/// switched off rather than retried on every event.</summary>
public sealed class GuardedRunHistorySink(
    IRunHistorySink inner, string providerName, int maxFailures, Action<string> log) : IRunHistorySink
{
    private int _failures;
    private bool _disabled;

    public void Emit(FactoryEvent evt) => Attempt(() => inner.Emit(evt));

    public void Flush() => Attempt(inner.Flush);

    private void Attempt(Action action)
    {
        if (_disabled) return;

        try
        {
            action();
        }
        catch (Exception ex)
        {
            _failures++;
            log($"sink '{providerName}' failed ({_failures}/{maxFailures}): {ex.Message}");

            if (_failures < maxFailures) return;
            _disabled = true;
            log($"sink '{providerName}' disabled after {maxFailures} failures");
        }
    }
}
```

- [ ] **Step 6: Create `FanOutRunHistory`**

```csharp
using Factory.Core;

namespace Factory.Runtime;

/// <summary>Writes durably, then offers the event to every sink. Reads never touch a sink,
/// so an unreachable tracing backend cannot block a report.</summary>
public sealed class FanOutRunHistory(IRunHistory writer, IReadOnlyList<IRunHistorySink> sinks) : IRunHistory
{
    public void Append(FactoryEvent evt)
    {
        writer.Append(evt);
        foreach (var sink in sinks) sink.Emit(evt);
    }

    public IEnumerable<FactoryEvent> ReadFrom(long afterSeq) => writer.ReadFrom(afterSeq);
    public IReadOnlyList<RunRecord> RunsForItem(string itemId) => writer.RunsForItem(itemId);
    public IReadOnlyList<RunRecord> RunsForStation(string stationId) => writer.RunsForStation(stationId);
    public SpendTotals Totals() => writer.Totals();
    public BudgetRestoreView ForBudget() => writer.ForBudget();
    public IReadOnlyDictionary<string, string> Champions() => writer.Champions();

    public void Dispose()
    {
        foreach (var sink in sinks) sink.Flush();
        writer.Dispose();
    }
}
```

Ordering is deliberate: the durable write happens before any sink is offered the event, so a sink that throws cannot prevent the record from landing.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~GuardedProviderTests`
Expected: PASS, 3 tests.

- [ ] **Step 8: Commit**

```bash
git add src/Factory.Core/WorkItemStoreException.cs src/Factory.Runtime/Providers/ \
        tests/Factory.Tests/GuardedProviderTests.cs
git commit -m "Add guard decorators and sink fan-out for storage providers"
```

---

### Task 4: Plugin load context and catalog

**Files:**
- Create: `src/Factory.Runtime/Plugins/PluginLoadContext.cs`
- Create: `src/Factory.Runtime/Plugins/PluginCatalog.cs`
- Create: `src/Factory.Runtime/Plugins/ProviderRegistry.cs`
- Create: `tests/fixtures/Factory.TestPlugin/Factory.TestPlugin.csproj`
- Create: `tests/fixtures/Factory.TestPlugin/CountingSink.cs`
- Create: `tests/Factory.Tests/PluginCatalogTests.cs`
- Modify: `SoftwareFactory.sln` — add the fixture project

**Interfaces:**
- Consumes: `FactoryProviderAttribute` (Task 1), `ProviderRef` (Task 2).
- Produces: `ProviderRegistry` with `Register<T>(string name, Func<ProviderRef, T> factory)` and `T Resolve<T>(ProviderRef reference)`; `PluginCatalog.LoadInto(ProviderRegistry registry, string pluginsDir, Action<string> log)`.

The fixture plugin is built as a real DLL and loaded from disk. A mocked catalog would not exercise assembly loading, which is the part most likely to break.

- [ ] **Step 1: Create the fixture plugin project**

`tests/fixtures/Factory.TestPlugin/Factory.TestPlugin.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../../src/Factory.Core/Factory.Core.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>
</Project>
```

`Private=false` and `ExcludeAssets=runtime` stop `Factory.Core.dll` being copied next to the plugin. If it were copied, the plugin's load context could resolve its own copy and its `IRunHistorySink` would be a different type than the host's — the exact failure `PluginLoadContext` exists to prevent.

`tests/fixtures/Factory.TestPlugin/CountingSink.cs`:

```csharp
using Factory.Core;

namespace Factory.TestPlugin;

[FactoryProvider("counting", Contract = 1)]
public sealed class CountingSink : IRunHistorySink
{
    public static int Emitted;
    public void Emit(FactoryEvent evt) => Emitted++;
    public void Flush() { }
}
```

- [ ] **Step 2: Register the fixture in the solution**

```bash
dotnet sln SoftwareFactory.sln add tests/fixtures/Factory.TestPlugin/Factory.TestPlugin.csproj
```

- [ ] **Step 3: Write the failing tests**

Create `tests/Factory.Tests/PluginCatalogTests.cs`:

```csharp
using Factory.Core;
using Factory.Runtime;

namespace Factory.Tests;

public class PluginCatalogTests : IDisposable
{
    private readonly string _dir = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dir);

    /// <summary>Copies the built fixture plugin into a temp plugins directory.</summary>
    private string PluginsDirWithFixture()
    {
        var plugins = Path.Combine(_dir, "plugins");
        Directory.CreateDirectory(plugins);

        var source = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures",
            "Factory.TestPlugin", "bin", "Debug", "net10.0", "Factory.TestPlugin.dll");

        File.Copy(Path.GetFullPath(source), Path.Combine(plugins, "Factory.TestPlugin.dll"));
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

        // The cast is the assertion: a plugin that loaded its own Factory.Core would fail here.
        Assert.IsAssignableFrom<IRunHistorySink>(sink);
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

    private sealed class NoopSink : IRunHistorySink
    {
        public void Emit(FactoryEvent evt) { }
        public void Flush() { }
    }
}
```

- [ ] **Step 4: Run them to verify they fail**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~PluginCatalogTests`
Expected: FAIL — `ProviderRegistry` does not exist.

- [ ] **Step 5: Create `ProviderRegistry`**

```csharp
using Factory.Core;

namespace Factory.Runtime;

/// <summary>Names to provider factories. Built-ins are registered directly and plugins are
/// added on top, so selecting either is the same config change.</summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<(Type Port, string Name), Func<ProviderRef, object>> _factories = [];

    public void Register<T>(string name, Func<ProviderRef, T> factory) where T : class =>
        _factories[(typeof(T), name)] = reference => factory(reference);

    public T Resolve<T>(ProviderRef reference) where T : class
    {
        if (_factories.TryGetValue((typeof(T), reference.Provider), out var factory))
            return (T)factory(reference);

        var available = _factories.Keys
            .Where(k => k.Port == typeof(T))
            .Select(k => k.Name)
            .OrderBy(n => n);

        throw new InvalidOperationException(
            $"No {typeof(T).Name} provider named '{reference.Provider}'. " +
            $"Available: {string.Join(", ", available)}. " +
            $"Third-party providers load from the plugins directory.");
    }

    public bool Has<T>(string name) where T : class => _factories.ContainsKey((typeof(T), name));
}
```

- [ ] **Step 6: Create `PluginLoadContext`**

```csharp
using System.Reflection;
using System.Runtime.Loader;

namespace Factory.Runtime;

/// <summary>
/// Isolates a plugin's own dependencies while forcing contract types to come from the host.
/// Without that second rule a plugin loads its own <c>Factory.Core</c>, and the interface it
/// implements is a different type than the one the host asks for — which surfaces as an
/// unhelpful cast failure rather than a load error.
/// </summary>
internal sealed class PluginLoadContext(string pluginPath)
    : AssemblyLoadContext(name: Path.GetFileNameWithoutExtension(pluginPath), isCollectible: false)
{
    private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Contract assemblies always come from the default context so types unify.
        if (assemblyName.Name is "Factory.Core") return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
```

- [ ] **Step 7: Create `PluginCatalog`**

```csharp
using System.Reflection;
using Factory.Core;

namespace Factory.Runtime;

/// <summary>Discovers providers in <c>.factory/plugins/*.dll</c> and registers them by name.
/// A plugin that cannot be loaded is reported and skipped: one broken assembly must not stop
/// a factory whose configured providers are all built in.</summary>
public static class PluginCatalog
{
    public static void LoadInto(ProviderRegistry registry, string pluginsDir, Action<string> log)
    {
        if (!Directory.Exists(pluginsDir)) return;

        foreach (var dll in Directory.EnumerateFiles(pluginsDir, "*.dll").OrderBy(p => p))
        {
            try
            {
                RegisterAssembly(registry, dll, log);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ReflectionTypeLoadException)
            {
                log($"plugin '{Path.GetFileName(dll)}' could not be loaded: {ex.Message}");
            }
        }
    }

    private static void RegisterAssembly(ProviderRegistry registry, string dll, Action<string> log)
    {
        var context = new PluginLoadContext(dll);
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(dll));

        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.GetCustomAttribute<FactoryProviderAttribute>() is not { } marker) continue;

            if (marker.Contract != FactoryVersion.ContractVersion)
            {
                log($"plugin provider '{marker.Name}' targets contract v{marker.Contract}, " +
                    $"this factory implements v{FactoryVersion.ContractVersion} — skipped");
                continue;
            }

            if (Register<IRunHistorySink>(registry, type, marker.Name)) continue;
            if (Register<IWorkItemStore>(registry, type, marker.Name)) continue;
            if (Register<IRunHistory>(registry, type, marker.Name)) continue;

            log($"plugin type '{type.FullName}' is marked as a provider but implements no storage port — skipped");
        }
    }

    private static bool Register<T>(ProviderRegistry registry, Type type, string name) where T : class
    {
        if (!typeof(T).IsAssignableFrom(type)) return false;

        registry.Register<T>(name, reference => (T)(
            Activator.CreateInstance(type, reference) ??
            Activator.CreateInstance(type) ??
            throw new InvalidOperationException($"Provider '{name}' has no usable constructor.")));

        return true;
    }
}
```

Providers may take a `ProviderRef` constructor parameter to receive their options, or a parameterless one if they need none.

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet build && dotnet test tests/Factory.Tests --filter FullyQualifiedName~PluginCatalogTests`
Expected: PASS, 5 tests. The fixture project must build before the test runs — `dotnet build` at the solution root covers it.

- [ ] **Step 9: Commit**

```bash
git add src/Factory.Runtime/Plugins/ tests/fixtures/ tests/Factory.Tests/PluginCatalogTests.cs SoftwareFactory.sln
git commit -m "Load storage providers from plugin assemblies"
```

---

### Task 5: Wire provider selection into `FactoryHost`

**Files:**
- Modify: `src/Factory.Runtime/FactoryHost.cs:61-121`
- Test: `tests/Factory.Tests/PluginCatalogTests.cs` — add one host-level test

**Interfaces:**
- Consumes: everything from Tasks 1-4.
- Produces: no new public surface; `FactoryHost.Open` now resolves providers by config.

- [ ] **Step 1: Write the failing test**

Add to `PluginCatalogTests`:

```csharp
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
```

If the existing fake transport type in `tests/Factory.Tests` is named differently, use that name — check `RuntimeTests.cs` for how it constructs a host.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Factory.Tests --filter FullyQualifiedName~Host_falls_back`
Expected: FAIL — the host still constructs providers directly.

- [ ] **Step 3: Replace provider construction in `FactoryHost.Open`**

Replace the three lines added in phase 1 Task 4 Step 6:

```csharp
        var registry = new ProviderRegistry();
        var log2 = log ?? (_ => { });

        registry.Register<IRunHistory>("jsonl", _ => new JsonlRunHistory(paths.LedgerFile));
        PluginCatalog.LoadInto(registry, paths.PluginsDir, message => log2($"  [plugin] {message}"));

        var writer = registry.Resolve<IRunHistory>(new ProviderRef(config.RunHistory.Writer));

        var sinks = config.RunHistory.Sinks
            .Select(reference => (IRunHistorySink)new GuardedRunHistorySink(
                registry.Resolve<IRunHistorySink>(reference), reference.Provider, maxFailures: 3,
                message => log2($"  [sink] {message}")))
            .ToList();

        var history = new FanOutRunHistory(writer, sinks);
        var state = ((JsonlRunHistory)writer).Replay();

        registry.Register<IWorkItemStore>("ledger", _ => new LedgerWorkItemStore(history, state));

        var items = new GuardedWorkItemStore(
            registry.Resolve<IWorkItemStore>(config.WorkItemStore), config.WorkItemStore.Provider);
```

The `(JsonlRunHistory)writer` cast is a known wart: `Replay()` is not on `IRunHistory`, and
the cast would crash any non-JSONL writer. **Step 4 resolves it — do not stop after Step 3.**

**RULING (2026-08-14, after phase 1 shipped): do NOT add `Replay()` to `IRunHistory`.** This
plan originally preferred that; it is overruled. Use the static fold that already exists:

```csharp
        var state = FactoryState.Replay(history.ReadFrom(0));
```

`FactoryState.Replay(IEnumerable<FactoryEvent>)` is already public in `Factory.Core`, and it
is exactly what `JsonlRunHistory.Replay()` does internally. Reasons this beats a new port
member:

- `IRunHistory` is the **versioned plugin ABI**. A member added there is a contract every
  third-party provider must satisfy forever — to write a body all of them would copy verbatim.
  The ABI should carry what providers answer *differently*, not what they answer identically.
- Reconstructing `FactoryState` is domain policy, not storage. `ReadFrom` is the storage
  primitive; the fold belongs above it. Putting the fold on the port points the dependency the
  wrong way.
- The forwarding burden is already visible in this plan: `FanOutRunHistory` would have to add
  `public FactoryState Replay() => writer.Replay();` purely to delegate.
- It is one line at one call site, versus a permanent ABI change.

**The one argument that would reverse this:** if a provider could reconstruct state materially
faster than replaying every event — a database answering with a snapshot or a `GROUP BY` rather
than a full scan — then `Replay()` is genuinely provider-specific and belongs on the port, the
same reasoning that put `Totals()` and `ForBudget()` there under D4. Phase 1 shipped no such
provider and phase 3's beads store replays events like the JSONL one does. **Revisit if and
when a provider appears that can beat a full fold.**

- [ ] **Step 4: Drop the cast in favour of the existing static fold**

Per the ruling above, `IRunHistory` gains nothing. In Step 3's block, replace:

```csharp
        var state = ((JsonlRunHistory)writer).Replay();
```

with:

```csharp
        var state = FactoryState.Replay(history.ReadFrom(0));
```

Note it folds `history` (the `FanOutRunHistory`), not `writer` — reads flow through the
fan-out, which delegates them to the durable writer. `JsonlRunHistory.Replay()` stays as a
concrete convenience method; it keeps its existing callers and tests. Nothing is added to
`Factory.Core`.

- [ ] **Step 5: Run the full suite**

Run: `dotnet build && dotnet test`
Expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Factory.Runtime/FactoryHost.cs src/Factory.Core/IRunHistory.cs \
        src/Factory.Runtime/Providers/FanOutRunHistory.cs tests/Factory.Tests/PluginCatalogTests.cs
git commit -m "Resolve storage providers from configuration at host open"
```

---

### Task 6: Verification gate

- [ ] **Step 1: Confirm `Factory.Core` still has no dependencies**

Run: `grep -c "ProjectReference\|PackageReference" src/Factory.Core/Factory.Core.csproj`
Expected: `0`.

- [ ] **Step 2: Confirm the fixture plugin does not ship `Factory.Core.dll`**

Run: `ls tests/fixtures/Factory.TestPlugin/bin/Debug/net10.0/ | grep -c Factory.Core.dll`
Expected: `0`. A copy here means `Private=false` was lost and the type-unification test is passing by luck.

- [ ] **Step 3: Run the full gate and show the output**

Run: `dotnet build && dotnet test`
Expected: build succeeds, all tests pass. Paste the summary line into the completion report.

- [ ] **Step 4: Commit any fixes**

---

## Self-Review

**Spec coverage:** `FactoryProviderAttribute` with contract version — Task 1. Config binding with uniform built-in/plugin naming — Task 2. The two asymmetric guard boundaries (halt vs degrade) — Task 3. `PluginCatalog`, `PluginLoadContext`, `ProviderRegistry` — Task 4. Default-context resolution of `Factory.Core` — Task 4 Step 6, asserted in Task 4 Step 3's third test and Task 6 Step 2. Fan-out with the durable write ordered first — Task 3 Step 6.

**Deliberate omissions:** `isCollectible: false` on the load context — plugin unloading is not needed for a short-lived CLI process, and collectible contexts add real constraints for no benefit here. No plugin sandboxing: an in-process plugin is trusted code by construction, which was accepted when the in-process model was chosen.

**Known wart resolved in-plan:** Task 5 Step 3 introduces a downcast to `JsonlRunHistory` and Step 4 removes it by putting `Replay()` on the port. Do not stop after Step 3.

**Type consistency:** `ProviderRegistry.Register<T>`/`Resolve<T>` are keyed on `(Type, string)` and used with `IRunHistory`, `IRunHistorySink`, `IWorkItemStore` in Task 4 and Task 5. `GuardedRunHistorySink`'s four constructor parameters match every call site. `FanOutRunHistory(writer, sinks)` matches Task 5's construction.
