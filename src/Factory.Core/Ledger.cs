using System.Text;

namespace Factory.Core;

/// <summary>
/// Append-only JSONL event log. Current state is a fold over these events, so the factory
/// is crash-resumable and fully auditable: every item, every model call, every gate verdict,
/// and every prompt promotion is recorded in order.
/// </summary>
public sealed class Ledger : IDisposable
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private FileStream? _out;
    private long _seq;

    public string Path => _path;

    public Ledger(string path)
    {
        _path = path;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _seq = ReadAll().Select(e => e.Seq).DefaultIfEmpty(0).Max();
    }

    public T Append<T>(T evt) where T : FactoryEvent
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
        return evt;
    }

    public void AppendAll(IEnumerable<FactoryEvent> events)
    {
        foreach (var e in events) Append(e);
    }

    /// <summary>Reads every event. A torn final line (process killed mid-write) is skipped
    /// rather than treated as corruption.</summary>
    public IReadOnlyList<FactoryEvent> ReadAll()
    {
        if (!File.Exists(_path)) return [];

        var result = new List<FactoryEvent>();
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (FactoryJson.Read<FactoryEvent>(line) is { } evt) result.Add(evt);
            }
            catch (System.Text.Json.JsonException)
            {
                // Torn or unreadable line: skip it and keep the rest of the history.
            }
        }
        return result;
    }

    public FactoryState Replay() => FactoryState.Replay(ReadAll());

    public void Dispose()
    {
        lock (_gate)
        {
            _out?.Dispose();
            _out = null;
        }
    }
}
