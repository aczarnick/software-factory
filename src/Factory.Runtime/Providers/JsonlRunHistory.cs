using System.Text;
using Factory.Core;

namespace Factory.Runtime;

/// <summary>
/// Append-only JSONL event log. Current state is a fold over these events, so the factory
/// is crash-resumable and fully auditable: every item, every model call, every gate verdict,
/// and every prompt promotion is recorded in order.
/// </summary>
public sealed class JsonlRunHistory : IRunHistory
{
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private FileStream? _out;
    private long _seq;

    public string Path => _path;

    public JsonlRunHistory(string path, TimeProvider? clock = null)
    {
        _path = path;
        _clock = clock ?? TimeProvider.System;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _seq = ReadFrom(0).Select(e => e.Seq).DefaultIfEmpty(0).Max();
    }

    public void Append(FactoryEvent evt)
    {
        lock (_gate)
        {
            evt.Seq = ++_seq;
            var line = FactoryJson.Write<FactoryEvent>(evt) + "\n";
            _out ??= new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = Encoding.UTF8.GetBytes(line);
            _out.Write(bytes, 0, bytes.Length);
            _out.Flush(true);
        }
    }

    /// <summary>Reads every event after the given sequence. A torn final line (process killed
    /// mid-write) is skipped rather than treated as corruption.</summary>
    public IEnumerable<FactoryEvent> ReadFrom(long afterSeq)
    {
        if (!File.Exists(_path)) yield break;

        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            FactoryEvent? evt;
            try
            {
                evt = FactoryJson.Read<FactoryEvent>(line);
            }
            catch (System.Text.Json.JsonException)
            {
                // Torn or unreadable line: skip it and keep the rest of the history.
                continue;
            }

            if (evt is not null && evt.Seq > afterSeq) yield return evt;
        }
    }

    public IReadOnlyList<RunRecord> RunsForItem(string itemId) =>
        [.. Runs().Where(r => r.ItemId == itemId)];

    public IReadOnlyList<RunRecord> RunsForStation(string stationId) =>
        [.. Runs().Where(r => r.StationId == stationId)];

    public SpendTotals Totals()
    {
        var runs = Runs().ToList();
        return runs.Count == 0
            ? SpendTotals.Empty
            : new SpendTotals(
                runs.Count,
                runs.Sum(r => r.CostUsd),
                runs.Aggregate(TokenUsage.Zero, (a, r) => a + r.Usage));
    }

    public BudgetRestoreView ForBudget()
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var state = Replay();

        var perItem = new Dictionary<string, decimal>();
        decimal daily = 0m, evolutionDaily = 0m;

        foreach (var run in state.Runs)
        {
            perItem[run.ItemId] = perItem.GetValueOrDefault(run.ItemId) + run.CostUsd;
            if (DateOnly.FromDateTime(run.At.UtcDateTime) != today) continue;

            daily += run.CostUsd;
            if (state.Items.TryGetValue(run.ItemId, out var item) &&
                item.Provenance.Kind == ProvenanceKind.Evolution)
                evolutionDaily += run.CostUsd;
        }

        return new BudgetRestoreView(perItem, daily, evolutionDaily);
    }

    public IReadOnlyDictionary<string, string> Champions() => Replay().Champions;

    public FactoryState Replay() => FactoryState.Replay(ReadFrom(0));

    private IEnumerable<RunRecord> Runs() =>
        ReadFrom(0).OfType<RunCompleted>().Select(e => e.Record);

    public void Dispose()
    {
        lock (_gate)
        {
            _out?.Dispose();
            _out = null;
        }
    }
}
