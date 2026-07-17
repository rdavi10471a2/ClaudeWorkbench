using System.Security.Cryptography;
using System.Text;

namespace AIMonitor.Core;

public static class StableIdentifier
{
    public static string FromParts(string prefix, params string?[] parts)
    {
        string normalized = string.Join(
            "\u001f",
            parts.Select(part => (part ?? string.Empty).Replace('\\', '/').Trim().ToUpperInvariant()));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"{prefix}:{Convert.ToHexString(hash, 0, 12).ToLowerInvariant()}";
    }
}
