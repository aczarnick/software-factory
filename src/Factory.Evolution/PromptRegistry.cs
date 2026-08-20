using Factory.Core;

namespace Factory.Evolution;

public sealed record PromptVersion(string StationId, int Version, string Text)
{
    public string Id => $"{StationId}@v{Version}";
    public string Hash => Ids.Hash(Text);
}

/// <summary>Which version currently serves traffic for a station, and which challenger is
/// being trialled against it.</summary>
public sealed record PromptPointer
{
    public int Champion { get; init; } = 1;
    public int? Challenger { get; init; }

    /// <summary>Share of traffic routed to the challenger while it is under trial.</summary>
    public double ChallengerShare { get; init; } = 0.2;
}

/// <summary>
/// Versioned prompt storage. Prompts are assets with lineage, not string literals: every run
/// records the exact version that produced it, which is what makes prompt evaluation possible
/// and what lets a regression be rolled back to a known-good version.
/// </summary>
public sealed class PromptRegistry(string directory)
{
    private readonly Lock _gate = new();
    public string Directory { get; } = directory;

    private string StationDir(string stationId) => Path.Combine(Directory, stationId);
    private string PointerFile => Path.Combine(Directory, "pointers.json");

    private Dictionary<string, PromptPointer> LoadPointers()
    {
        if (!File.Exists(PointerFile)) return [];
        try
        {
            return FactoryJson.Read<Dictionary<string, PromptPointer>>(File.ReadAllText(PointerFile)) ?? [];
        }
        catch (System.Text.Json.JsonException) { return []; }
    }

    private void SavePointers(Dictionary<string, PromptPointer> pointers)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(PointerFile, FactoryJson.Write(pointers, pretty: true));
    }

    public PromptPointer Routing(string stationId) =>
        LoadPointers().GetValueOrDefault(stationId) ?? new PromptPointer();

    public IReadOnlyList<PromptVersion> Versions(string stationId)
    {
        var dir = StationDir(stationId);
        if (!System.IO.Directory.Exists(dir)) return [];

        return System.IO.Directory.GetFiles(dir, "v*.md")
            .Select(f => (Path: f, Version: ParseVersion(Path.GetFileNameWithoutExtension(f))))
            .Where(x => x.Version > 0)
            .OrderBy(x => x.Version)
            .Select(x => new PromptVersion(stationId, x.Version, File.ReadAllText(x.Path)))
            .ToList();
    }

    private static int ParseVersion(string name) =>
        name.StartsWith('v') && int.TryParse(name[1..], out var n) ? n : 0;

    public PromptVersion? Get(string stationId, int version)
    {
        var path = Path.Combine(StationDir(stationId), $"v{version}.md");
        return File.Exists(path) ? new PromptVersion(stationId, version, File.ReadAllText(path)) : null;
    }

    public PromptVersion Champion(string stationId)
    {
        var ptr = Routing(stationId);
        var versions = Versions(stationId);
        return Get(stationId, ptr.Champion)
            ?? (versions.Count > 0 ? versions[^1] : null)
            ?? throw new InvalidOperationException(
                $"No prompt registered for station '{stationId}'. Run `factory init` to seed the kit.");
    }

    public PromptVersion? Challenger(string stationId)
    {
        var ptr = Routing(stationId);
        return ptr.Challenger is { } v ? Get(stationId, v) : null;
    }

    /// <summary>Picks the version to serve this run. While a challenger is under trial it
    /// receives a slice of traffic so both arms accumulate evidence under identical
    /// conditions.</summary>
    public PromptVersion Select(string stationId, Random rng)
    {
        var ptr = Routing(stationId);
        if (ptr.Challenger is { } cv && rng.NextDouble() < ptr.ChallengerShare && Get(stationId, cv) is { } challenger)
            return challenger;
        return Champion(stationId);
    }

    /// <summary>Registers a new version. Identical text is deduplicated so retries do not
    /// inflate the lineage.</summary>
    public PromptVersion Add(string stationId, string text)
    {
        lock (_gate)
        {
            var existing = Versions(stationId);
            if (existing.FirstOrDefault(v => v.Text == text) is { } dupe) return dupe;

            var next = existing.Count == 0 ? 1 : existing.Max(v => v.Version) + 1;
            var dir = StationDir(stationId);
            System.IO.Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"v{next}.md"), text);
            return new PromptVersion(stationId, next, text);
        }
    }

    /// <summary>Seeds a station's v1 from the shipped kit if nothing is registered yet.</summary>
    public PromptVersion EnsureSeed(string stationId, string defaultText)
    {
        var existing = Versions(stationId);
        if (existing.Count > 0) return Champion(stationId);

        var v = Add(stationId, defaultText);
        SetChampion(stationId, v.Version);
        return v;
    }

    public void SetChampion(string stationId, int version)
    {
        lock (_gate)
        {
            var pointers = LoadPointers();
            var current = pointers.GetValueOrDefault(stationId) ?? new PromptPointer();
            pointers[stationId] = current with { Champion = version, Challenger = null };
            SavePointers(pointers);
        }
    }

    public void SetChallenger(string stationId, int version, double share = 0.2)
    {
        lock (_gate)
        {
            var pointers = LoadPointers();
            var current = pointers.GetValueOrDefault(stationId) ?? new PromptPointer();
            pointers[stationId] = current with { Challenger = version, ChallengerShare = share };
            SavePointers(pointers);
        }
    }

    public void ClearChallenger(string stationId)
    {
        lock (_gate)
        {
            var pointers = LoadPointers();
            if (pointers.GetValueOrDefault(stationId) is { } current)
            {
                pointers[stationId] = current with { Challenger = null };
                SavePointers(pointers);
            }
        }
    }
}
