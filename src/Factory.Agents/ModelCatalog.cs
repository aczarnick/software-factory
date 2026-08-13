using Factory.Core;

namespace Factory.Agents;

/// <summary>
/// Maps station tiers onto models. Aliases are used rather than pinned ids so the factory
/// tracks the latest model in each tier without a code change; an explicit override is
/// available per tier via environment for pinning.
/// </summary>
public static class ModelCatalog
{
    public static string Resolve(ModelTier tier) => tier switch
    {
        ModelTier.Haiku => Env("FACTORY_MODEL_HAIKU", "haiku"),
        ModelTier.Sonnet => Env("FACTORY_MODEL_SONNET", "sonnet"),
        ModelTier.Opus => Env("FACTORY_MODEL_OPUS", "opus"),
        ModelTier.None => throw new InvalidOperationException(
            "ModelTier.None stations are deterministic and must not invoke a model."),
        _ => "sonnet"
    };

    private static string Env(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;
}
