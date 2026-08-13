using Factory.Core;

namespace Factory.Agents;

/// <summary>
/// Content-addressed cache over agent responses. A hit skips the model call entirely, which
/// is the only token-reduction mechanism that saves 100% rather than a percentage.
///
/// The key covers the profile, model, system prompt, tools, prompt text, output schema, and
/// a caller-supplied digest of the workspace state the prompt depends on — so a hit means
/// the same question about the same world, not merely the same words.
/// </summary>
public sealed class ResponseCache(string directory, TimeSpan? ttl = null)
{
    private readonly TimeSpan _ttl = ttl ?? TimeSpan.FromDays(7);

    public string Directory { get; } = directory;
    public int Hits { get; private set; }
    public int Misses { get; private set; }

    private sealed record Entry(AgentRunResult Result, DateTimeOffset StoredAt);

    private string PathFor(string key) => Path.Combine(Directory, $"{key}.json");

    public bool TryGet(AgentRequest request, out AgentRunResult result)
    {
        result = default!;
        if (request.NoCache) { Misses++; return false; }

        var path = PathFor(request.CacheKey);
        if (!File.Exists(path)) { Misses++; return false; }

        try
        {
            var entry = FactoryJson.Read<Entry>(File.ReadAllText(path));
            if (entry is null) { Misses++; return false; }

            if (DateTimeOffset.UtcNow - entry.StoredAt > _ttl)
            {
                File.Delete(path);
                Misses++;
                return false;
            }

            Hits++;
            result = entry.Result with { CacheHit = true, DurationMs = 0 };
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            Misses++;
            return false;
        }
    }

    public void Put(AgentRequest request, AgentRunResult result)
    {
        // Only successful runs are worth replaying; caching a failure would pin a bad outcome.
        if (request.NoCache || !result.Success) return;

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var entry = new Entry(result with { CacheHit = false }, DateTimeOffset.UtcNow);
            var tmp = PathFor(request.CacheKey) + ".tmp";
            File.WriteAllText(tmp, FactoryJson.Write(entry));
            File.Move(tmp, PathFor(request.CacheKey), overwrite: true);
        }
        catch (IOException)
        {
            // A cache that cannot write is a slow cache, not a broken factory.
        }
    }

    public double HitRate => Hits + Misses == 0 ? 0 : (double)Hits / (Hits + Misses);
}
