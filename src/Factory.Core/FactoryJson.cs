using System.Text.Json;
using System.Text.Json.Serialization;

namespace Factory.Core;

public static class FactoryJson
{
    /// <summary>Compact form used for the ledger (one event per line — never indented).</summary>
    public static readonly JsonSerializerOptions Compact = Build(indented: false);

    /// <summary>Human-facing form used for config files and CLI output.</summary>
    public static readonly JsonSerializerOptions Pretty = Build(indented: true);

    private static JsonSerializerOptions Build(bool indented) => new()
    {
        WriteIndented = indented,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Write<T>(T value, bool pretty = false) =>
        JsonSerializer.Serialize(value, pretty ? Pretty : Compact);

    public static T? Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Compact);
}
