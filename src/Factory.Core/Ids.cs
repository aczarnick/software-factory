using System.Security.Cryptography;
using System.Text;

namespace Factory.Core;

public static class Ids
{
    public static string New(string prefix) => $"{prefix}_{Guid.NewGuid().ToString("n")[..12]}";

    /// <summary>Stable short content hash, used for prompt versions and cache keys.</summary>
    public static string Hash(params string?[] parts)
    {
        var joined = string.Join("", parts.Select(p => p ?? ""));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}
