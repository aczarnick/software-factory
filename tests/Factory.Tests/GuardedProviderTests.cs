using System.Collections.Concurrent;
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
        public WorkItem Transition(WorkItem item, WorkItemState target, string? reason) => throw new InvalidOperationException("store down");
        public WorkItem? Get(string id) => throw new InvalidOperationException("store down");
        public IReadOnlyList<WorkItem> All() => throw new InvalidOperationException("store down");
        public WorkItem? TryClaim(string owner) => throw new InvalidOperationException("store down");
        public void Heartbeat(string id) => throw new InvalidOperationException("store down");
        public void Release(string id, string reason) => throw new InvalidOperationException("store down");
        public void Sync() => throw new InvalidOperationException("store down");
        public IReadOnlyList<WorkItem> Reclaim(TimeSpan olderThan) => throw new InvalidOperationException("store down");
    }

    /// <summary>Holds every caller inside <see cref="Emit"/> until all of them have arrived,
    /// so they enter the guard's failure bookkeeping at the same moment.</summary>
    private sealed class SimultaneouslyFailingSink(Barrier rendezvous) : IRunHistorySink
    {
        public void Emit(FactoryEvent evt)
        {
            rendezvous.SignalAndWait();
            throw new InvalidOperationException("sink down");
        }

        public void Flush() { }
    }

    private sealed class ThrowingFlushSink : IRunHistorySink
    {
        public void Emit(FactoryEvent evt) { }
        public void Flush() => throw new InvalidOperationException("flush down");
    }

    private sealed class RecordingHistory : IRunHistory
    {
        public bool Disposed;
        public void Append(FactoryEvent evt) { }
        public IEnumerable<FactoryEvent> ReadFrom(long afterSeq) => [];
        public IReadOnlyList<RunRecord> RunsForItem(string itemId) => [];
        public IReadOnlyList<RunRecord> RunsForStation(string stationId) => [];
        public SpendTotals Totals() => SpendTotals.Empty;
        public BudgetRestoreView ForBudget() => BudgetRestoreView.Empty;
        public IReadOnlyDictionary<string, string> Champions() => new Dictionary<string, string>();
        public void Dispose() => Disposed = true;
    }

    private sealed class ObservingSink(IRunHistory history) : IRunHistorySink
    {
        public int RecordsVisibleAtEmit = -1;
        public void Emit(FactoryEvent evt) => RecordsVisibleAtEmit = history.ReadFrom(0).Count();
        public void Flush() { }
    }

    [Fact]
    public void AFailingStoreHaltsWithANamedException()
    {
        var store = new GuardedWorkItemStore(new ThrowingStore(), "beads");

        var ex = Assert.Throws<WorkItemStoreException>(() => store.All());

        Assert.Contains("beads", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void AFailingSinkIsDisabledAfterTheFailureCeiling()
    {
        var inner = new ThrowingSink();
        var warnings = new List<string>();
        var sink = new GuardedRunHistorySink(inner, "tracer", maxFailures: 2, warnings.Add);

        for (var i = 0; i < 5; i++) sink.Emit(new FactoryNote("x"));

        Assert.Equal(2, inner.EmitCount);
        Assert.Contains(warnings, w => w.Contains("disabled"));
    }

    [Fact]
    public void FanOutStillWritesDurablyWhenEverySinkFails()
    {
        var dir = TempDir.Create();
        try
        {
            using var writer = new JsonlRunHistory(Path.Combine(dir, "ledger.jsonl"));
            var sink = new GuardedRunHistorySink(new ThrowingSink(), "tracer", 1, _ => { });
            using var history = new FanOutRunHistory(writer, [sink]);

            history.Append(new FactoryNote("survives"));

            Assert.Single(history.ReadFrom(0));
        }
        finally { TempDir.Delete(dir); }
    }

    [Fact]
    public void DisposeStillDisposesTheWriterWhenASinkFlushThrows()
    {
        using var writer = new RecordingHistory();
        FanOutRunHistory? history = new FanOutRunHistory(writer, [new ThrowingFlushSink()]);
        try
        {
            // A flush that throws must still leave the writer disposed, via FanOutRunHistory's
            // own finally block.
            Assert.Throws<InvalidOperationException>(() => history.Dispose());
            history = null;
        }
        finally
        {
            history?.Dispose();
        }

        Assert.True(writer.Disposed);
    }

    [Fact]
    public void FanOutWritesDurablyBeforeOfferingTheEventToSinks()
    {
        var dir = TempDir.Create();
        try
        {
            using var writer = new JsonlRunHistory(Path.Combine(dir, "ledger.jsonl"));
            var sink = new ObservingSink(writer);
            using var history = new FanOutRunHistory(writer, [sink]);

            history.Append(new FactoryNote("ordering"));

            Assert.Equal(1, sink.RecordsVisibleAtEmit);
        }
        finally { TempDir.Delete(dir); }
    }

    [Fact]
    public void ConcurrentFailuresAreCountedOnceEachAndDisableTheSinkOnce()
    {
        const int callers = 8;
        using var rendezvous = new Barrier(callers);
        var warnings = new ConcurrentBag<string>();
        var sink = new GuardedRunHistorySink(
            new SimultaneouslyFailingSink(rendezvous), "tracer", maxFailures: callers, warnings.Add);

        var threads = Enumerable.Range(0, callers)
            .Select(_ => new Thread(() => sink.Emit(new FactoryNote("x"))))
            .ToList();
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        var counted = warnings.Where(w => w.Contains("failed (")).ToList();
        Assert.Equal(callers, counted.Count);
        Assert.Equal(callers, counted.Distinct().Count());
        Assert.Single(warnings, w => w.Contains("disabled"));
    }
}
