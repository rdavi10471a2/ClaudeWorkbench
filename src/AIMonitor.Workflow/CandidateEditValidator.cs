using AIMonitor.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text;
using System.Text.Json;

namespace AIMonitor.Workflow;

internal sealed class CandidateEditValidator
{
    private const string NewFileHash = "<new-file>";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WorkflowEditPaths paths;
    private readonly Dictionary<string, EditOverlayValidationResult> overlayValidationCache = new(StringComparer.Ordinal);

    public CandidateEditValidator(MonitorSettings settings)
    {
        paths = new WorkflowEditPaths(settings);
    }

    public EditSyntaxValidationResult ValidateSyntaxIfCSharp(string watchedFilePath, string content)
    {
        if (!watchedFilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return new EditSyntaxValidationResult(false, []);
        }

        SyntaxTree tree = CSharpSyntaxTree.ParseText(content, path: watchedFilePath);
        EditSyntaxDiagnostic[] diagnostics = tree.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic =>
            {
                FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
                return new EditSyntaxDiagnostic(
                    diagnostic.Id,
                    diagnostic.GetMessage(),
                    span.StartLinePosition.Line + 1,
                    span.StartLinePosition.Character + 1);
            })
            .ToArray();

        return new EditSyntaxValidationResult(diagnostics.Length > 0, diagnostics);
    }

    public EditOverlayValidationResult ValidateCandidateOverlayCompilation(
        EditSessionManifest manifest,
        string candidateFilePath)
    {
        if (manifest.WatchedFilePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
            || manifest.WatchedFilePath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
        {
            return new EditOverlayValidationResult("razor-validation-pending", false, 0, 0, []);
        }

        if (!manifest.WatchedFilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return new EditOverlayValidationResult("skipped-non-csharp", false, 0, 0, []);
        }

        try
        {
            string watchedRoot = Path.GetFullPath(paths.Settings.WatchedProjectFolder);
            CSharpParseOptions parseOptions = new(
                languageVersion: LanguageVersion.Latest,
                kind: SourceCodeKind.Regular,
                documentationMode: DocumentationMode.Parse);
            Dictionary<string, string> overlays = BuildCandidateOverlayMap(manifest, candidateFilePath);
            string[] overlayRelatives = overlays.Keys.ToArray();
            string cacheKey = BuildOverlayValidationCacheKey(overlays);
            if (overlayValidationCache.TryGetValue(cacheKey, out EditOverlayValidationResult? cached))
            {
                return cached with { FromCache = true };
            }

            List<SyntaxTree> trees = [];
            int overlayFileCount = 0;
            foreach (string sourcePath in EnumerateObservedSourceFiles(watchedRoot))
            {
                string relative = NormalizePath(Path.GetRelativePath(watchedRoot, sourcePath));
                string textPath = sourcePath;
                if (overlays.Remove(relative, out string? overlayPath))
                {
                    textPath = overlayPath;
                    overlayFileCount++;
                }

                trees.Add(CSharpSyntaxTree.ParseText(
                    File.ReadAllText(textPath),
                    parseOptions,
                    path: Path.GetFullPath(sourcePath)));
            }

            foreach (KeyValuePair<string, string> overlay in overlays)
            {
                string treePath = Path.GetFullPath(Path.Combine(
                    watchedRoot,
                    overlay.Key.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)));
                trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(overlay.Value), parseOptions, treePath));
                overlayFileCount++;
            }

            trees.Add(BuildImplicitGlobalUsingsTree(parseOptions, watchedRoot));
            trees.Add(BuildWinFormsBootstrapTree(parseOptions));

            CSharpCompilation compilation = CSharpCompilation.Create(
                "AIMonitorCandidateOverlayValidation",
                trees,
                GetMetadataReferences(watchedRoot),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            HashSet<string> diagnosticPaths = BuildOverlayTreePaths(watchedRoot, manifest, overlayRelatives)
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            EditOverlayDiagnostic[] diagnostics = compilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Where(diagnostic => diagnostic.Location.IsInSource)
                .Where(diagnostic =>
                {
                    string path = diagnostic.Location.GetLineSpan().Path;
                    return !string.IsNullOrWhiteSpace(path)
                        && diagnosticPaths.Contains(Path.GetFullPath(path));
                })
                .Take(50)
                .Select(diagnostic =>
                {
                    FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
                    return new EditOverlayDiagnostic(
                        diagnostic.Id,
                        diagnostic.GetMessage(),
                        span.Path,
                        span.StartLinePosition.Line + 1,
                        span.StartLinePosition.Character + 1);
                })
                .ToArray();

            EditOverlayValidationResult result = new(
                diagnostics.Length > 0 ? "compiled-with-errors" : "compiled",
                diagnostics.Length > 0,
                trees.Count,
                overlayFileCount,
                diagnostics);
            overlayValidationCache[cacheKey] = result;
            return result;
        }
        catch (Exception ex)
        {
            return new EditOverlayValidationResult(
                "validation-failed",
                true,
                0,
                0,
                [new EditOverlayDiagnostic("AIMONITOR_CANDIDATE_OVERLAY", ex.Message, manifest.WatchedFilePath, 0, 0)]);
        }
    }

    private Dictionary<string, string> BuildCandidateOverlayMap(EditSessionManifest manifest, string candidateFilePath)
    {
        Dictionary<string, string> overlays = new(StringComparer.OrdinalIgnoreCase)
        {
            [NormalizePath(manifest.RelativePath)] = candidateFilePath
        };

        if (!Directory.Exists(paths.MetadataRoot))
        {
            return overlays;
        }

        foreach (string manifestPath in Directory.EnumerateFiles(paths.MetadataRoot, "*.json", SearchOption.AllDirectories))
        {
            EditSessionManifest? otherManifest;
            try
            {
                otherManifest = JsonSerializer.Deserialize<EditSessionManifest>(File.ReadAllText(manifestPath), JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            // The overlay is a C#-only Roslyn compile: only .cs candidates belong in the map. A non-.cs
            // working file from another session (.razor, .md, .json, etc.) would be ParseText'd as C# and
            // emit spurious CS errors (e.g. Docs\*.md parsed as C#). Skip anything that is not .cs.
            if (otherManifest is null
                || !File.Exists(otherManifest.WorkingFilePath)
                || NormalizePath(otherManifest.RelativePath).Equals(NormalizePath(manifest.RelativePath), StringComparison.OrdinalIgnoreCase)
                || !otherManifest.RelativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || !IsCandidateBaselineCurrent(otherManifest))
            {
                continue;
            }

            overlays[NormalizePath(otherManifest.RelativePath)] = otherManifest.WorkingFilePath;
        }

        return overlays;
    }

    private static bool IsCandidateBaselineCurrent(EditSessionManifest manifest)
    {
        if (manifest.OriginalHash.Equals(NewFileHash, StringComparison.OrdinalIgnoreCase))
        {
            return !File.Exists(manifest.WatchedFilePath);
        }

        return File.Exists(manifest.WatchedFilePath)
            && manifest.OriginalHash.Equals(FileHash.Compute(manifest.WatchedFilePath), StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildOverlayValidationCacheKey(IReadOnlyDictionary<string, string> overlays)
    {
        StringBuilder builder = new();
        foreach (KeyValuePair<string, string> overlay in overlays.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(NormalizePath(overlay.Key));
            builder.Append('=');
            builder.Append(File.Exists(overlay.Value) ? FileHash.Compute(overlay.Value) : "<missing>");
            builder.Append('|');
        }

        return builder.ToString();
    }

    private static IEnumerable<string> BuildOverlayTreePaths(
        string watchedRoot,
        EditSessionManifest manifest,
        IEnumerable<string> overlayRelatives)
    {
        yield return Path.GetFullPath(manifest.WatchedFilePath);
        foreach (string relative in overlayRelatives)
        {
            yield return Path.GetFullPath(Path.Combine(
                watchedRoot,
                relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)));
        }
    }

    private static IEnumerable<string> EnumerateObservedSourceFiles(string watchedRoot)
    {
        Stack<string> pending = new();
        pending.Push(watchedRoot);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach (string directory in Directory.EnumerateDirectories(current))
            {
                if (!IsIgnoredSourceDirectory(directory))
                {
                    pending.Push(directory);
                }
            }

            foreach (string file in Directory.EnumerateFiles(current, "*.cs"))
            {
                yield return file;
            }
        }
    }

    private static bool IsIgnoredSourceDirectory(string path)
    {
        string name = Path.GetFileName(path);
        return name.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".vs", StringComparison.OrdinalIgnoreCase)
            || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || name.Equals("packages", StringComparison.OrdinalIgnoreCase)
            || name.Equals("runtime", StringComparison.OrdinalIgnoreCase)
            || name.Equals("archive", StringComparison.OrdinalIgnoreCase);
    }

    private static SyntaxTree BuildImplicitGlobalUsingsTree(CSharpParseOptions parseOptions, string watchedRoot)
    {
        HashSet<string> usings = new(StringComparer.Ordinal)
        {
            "global using System;",
            "global using System.Collections.Generic;",
            "global using System.ComponentModel;",
            "global using System.IO;",
            "global using System.Linq;",
            "global using System.Net.Http;",
            "global using System.Threading;",
            "global using System.Threading.Tasks;"
        };

        foreach (string generatedUsing in LoadGeneratedGlobalUsings(watchedRoot))
        {
            usings.Add(generatedUsing);
        }

        string text = string.Join(Environment.NewLine, usings.OrderBy(value => value, StringComparer.Ordinal));
        return CSharpSyntaxTree.ParseText(text, parseOptions, path: "<AIMonitor_ImplicitGlobalUsings.g.cs>");
    }

    private static SyntaxTree BuildWinFormsBootstrapTree(CSharpParseOptions parseOptions)
    {
        const string text = """
            namespace System.Windows.Forms
            {
                internal static class ApplicationConfiguration
                {
                    public static void Initialize() { }
                }
            }
            """;
        return CSharpSyntaxTree.ParseText(text, parseOptions, path: "<AIMonitor_WinFormsBootstrap.g.cs>");
    }

    private static IEnumerable<string> LoadGeneratedGlobalUsings(string watchedRoot)
    {
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(watchedRoot, "*GlobalUsings.g.cs", SearchOption.AllDirectories)
                .Where(path => path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch
        {
            yield break;
        }

        foreach (string path in candidates)
        {
            foreach (string line in File.ReadLines(path))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("global using ", StringComparison.Ordinal))
                {
                    yield return trimmed.EndsWith(';') ? trimmed : trimmed + ";";
                }
            }
        }
    }

    private static List<MetadataReference> GetMetadataReferences(string watchedRoot)
    {
        Dictionary<string, MetadataReference> references = new(StringComparer.OrdinalIgnoreCase);
        string? tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrWhiteSpace(tpa))
        {
            foreach (string path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                AddReference(references, path);
            }
        }

        foreach (string path in EnumerateObservedBinaryReferences(watchedRoot))
        {
            AddReference(references, path);
        }

        foreach (string path in EnumerateWindowsDesktopReferences())
        {
            AddReference(references, path);
        }

        return references.Values.ToList();
    }

    private static IEnumerable<string> EnumerateObservedBinaryReferences(string watchedRoot)
    {
        string binRoot = Path.Combine(watchedRoot, "bin");
        if (!Directory.Exists(binRoot))
        {
            yield break;
        }

        foreach (string dllPath in Directory.EnumerateFiles(binRoot, "*.dll", SearchOption.AllDirectories))
        {
            yield return dllPath;
        }
    }

    private static IEnumerable<string> EnumerateWindowsDesktopReferences()
    {
        string desktopRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet",
            "shared",
            "Microsoft.WindowsDesktop.App");
        if (!Directory.Exists(desktopRoot))
        {
            yield break;
        }

        DirectoryInfo? latest = Directory.EnumerateDirectories(desktopRoot)
            .Select(path => new DirectoryInfo(path))
            .OrderByDescending(directory => directory.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (latest is null)
        {
            yield break;
        }

        foreach (string dllPath in Directory.EnumerateFiles(latest.FullName, "*.dll"))
        {
            yield return dllPath;
        }
    }

    private static void AddReference(Dictionary<string, MetadataReference> references, string path)
    {
        try
        {
            if (File.Exists(path))
            {
                references[Path.GetFullPath(path)] = MetadataReference.CreateFromFile(path);
            }
        }
        catch
        {
            // Best effort only; one bad reference should not hide candidate diagnostics from other files.
        }
    }

    private static string NormalizePath(string path)
    {
        return path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }
}
