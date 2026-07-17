using System.Security.Cryptography;
using System.Text;

namespace AIMonitor.Core;

public static class MonitorWorkspacePaths
{
    public static string GetWatchedSolutionWorkspaceRoot(MonitorSettings settings)
    {
        string solutionName = Path.GetFileNameWithoutExtension(settings.WatchedSolutionPath);
        string safeName = GetSafePathSegment(solutionName);
        string identity = GetPathIdentity(settings.WatchedSolutionPath);
        return Path.Combine(settings.RuntimeRoot, "watched-solutions", $"{safeName}-{identity}");
    }

    public static string GetSafePathSegment(string value)
    {
        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        StringBuilder builder = new(value.Length);

        foreach (char character in value)
        {
            builder.Append(invalidCharacters.Contains(character) ? '_' : character);
        }

        string safe = builder.ToString().Trim('.', ' ');
        return string.IsNullOrWhiteSpace(safe) ? "solution" : safe;
    }

    private static string GetPathIdentity(string path)
    {
        string normalizedPath = Path.GetFullPath(path).ToUpperInvariant();
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
        return Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
    }
}
