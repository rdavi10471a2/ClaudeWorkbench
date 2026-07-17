using System.Security.Cryptography;

namespace AIMonitor.Workflow;

public static class FileHash
{
    public static string Compute(string filePath)
    {
        using FileStream stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string ComputeText(string text)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(NormalizeLineEndings(text));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static string ComputeNormalizedFile(string filePath)
    {
        return ComputeText(File.ReadAllText(filePath));
    }

    private static string NormalizeLineEndings(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
    }
}
