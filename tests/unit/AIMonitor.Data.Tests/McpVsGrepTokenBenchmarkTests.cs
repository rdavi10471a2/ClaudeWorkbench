using AIMonitor.Core;
using AIMonitor.MSBuild;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIMonitor.Data.Tests;

// Full-coverage token benchmark: for EVERY indexed symbol, measure the context an agent must ingest to answer
// "where is X defined + all its references" via the MCP index path vs via grep+read. Local-only (skips unless the
// bench solution exists), headless, writes CSV + summary under runtime/token-benchmark.
//
// Faithfulness:
//   MCP arm calls the SAME query path the tools use — FindIndexedSymbols -> FindSymbols, FindIndexedReferences ->
//   ListReferences().Take() — serialized as compact camelCase JSON (JsonSerializerDefaults.Web), matching observed
//   tool responses. So the planned fixes flow through on rebuild+rerun:
//     Fix #1 (qualified Type.Member lookup): the member arm queries the QUALIFIED "Type.Member" first; today that
//            returns 0 and forces a fallback to the bare name (all homonyms). Once FindSymbols resolves the qualified
//            form, the fallback disappears and lookup cost collapses — captured automatically.
//     Fix #2 (leaner reference-row shape): references serialize from the real IndexedReferenceRow, so trimming fields
//            shrinks the measured payload automatically.
//   grep arm is implemented in-process (no external ripgrep dependency) as a case-sensitive word-boundary search,
//   reproducing `rg -n -w` (plain match lines) and `rg -C3` (context) byte output, plus distinct-hit-file sizes.
//   Word-boundary uses \b so it reproduces the real miss modes (e.g. \bSourceTable\b does NOT match "SourceTables").
public sealed class McpVsGrepTokenBenchmarkTests
{
    private const string DefaultBenchSolution = @"C:\VSCodeProjects\SchemaStudioBench\SchemaStudioWebViewer.sln";
    private const string DefaultOutputDir = @"C:\VSCodeProjects\AIMonitor\runtime\token-benchmark";

    [Fact]
    [Trait("Suite", "TokenBenchmark")]
    public async Task FullCoverage_mcp_vs_grep_token_baseline()
    {
        string solutionPath = Environment.GetEnvironmentVariable("BENCH_SOLUTION") ?? DefaultBenchSolution;
        if (!File.Exists(solutionPath))
        {
            return; // local-only ground-truth: skip when the bench copy isn't on this machine
        }

        string sourceRoot = Path.GetDirectoryName(solutionPath)!;
        string outputDir = Environment.GetEnvironmentVariable("BENCH_OUT") ?? DefaultOutputDir;
        Directory.CreateDirectory(outputDir);

        // Fresh index of the bench copy via the real build/index path.
        string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorTokenBench", Guid.NewGuid().ToString("N"));
        MonitorSettings settings = MonitorSettings.Create(tempRoot, solutionPath, Path.Combine(tempRoot, "runtime"));
        string databasePath = MonitorDataPaths.GetDefaultIndexDatabasePath(settings);
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));
        await new SolutionIndexBuilder(new MSBuildWorkspaceLoader(), store).RebuildAsync(settings);

        SolutionIndexQueryService query = SolutionIndexQueryService.Create(settings);
        JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

        IReadOnlyList<IndexedSymbolQueryItem> allSymbols = query.FindSymbols(string.Empty, maxResults: 50000).Symbols;
        IReadOnlyList<IndexedReferenceRow> allReferences = query.ListReferences(null);
        ILookup<string, IndexedReferenceRow> referencesByTarget =
            allReferences.ToLookup(reference => reference.TargetStableKey, StringComparer.Ordinal);

        List<SourceFile> sourceFiles = LoadSourceFiles(sourceRoot);
        Console.WriteLine($"[token-bench] loaded {sourceFiles.Count} searchable files; {allSymbols.Count} symbols; {allReferences.Count} references.");

        Dictionary<string, (int Total, int Bytes)> findCache = new(StringComparer.Ordinal);
        Dictionary<string, GrepCost> grepCache = new(StringComparer.Ordinal);

        (int Total, int Bytes) Find(string text)
        {
            if (findCache.TryGetValue(text, out (int Total, int Bytes) cached))
            {
                return cached;
            }

            IndexedSymbolSearchResult result = query.FindSymbols(text, maxResults: 100);
            int bytes = JsonSerializer.Serialize(result, json).Length;
            (int Total, int Bytes) value = (result.TotalSymbolCount, bytes);
            findCache[text] = value;
            return value;
        }

        StringBuilder csv = new();
        csv.AppendLine("name,kind,refCount,mcpBytes,mcpTokens,grepMinBytes,grepMinTokens,grepFullBytes,grepFullTokens,grepFiles,fullRatio,minRatio,isMember,qualifiedResolvesToday");

        long sumMcp = 0;
        long sumGrepMin = 0;
        long sumGrepFull = 0;
        int total = 0;
        int winFull = 0;
        int winMin = 0;
        int grepZeroHit = 0;
        int memberCount = 0;
        int memberQualifiedZeroToday = 0;
        int errors = 0;
        List<double> fullRatios = new();
        List<(string Name, long GrepFull, long Mcp)> worst = new();

        Stopwatch stopwatch = Stopwatch.StartNew();
        foreach (IndexedSymbolQueryItem item in allSymbols)
        {
            IndexedSymbolRow symbol = item.Symbol;
            string name = symbol.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            try
            {
                string containingSimple = string.Empty;
                if (!string.IsNullOrEmpty(symbol.ContainingType))
                {
                    int dot = symbol.ContainingType.LastIndexOf('.');
                    containingSimple = dot >= 0 ? symbol.ContainingType[(dot + 1)..] : symbol.ContainingType;
                }

                bool isMember = containingSimple.Length > 0;
                string qualifiedQuery = isMember ? containingSimple + "." + name : name;

                // MCP lookup cost: qualified first (realistic), fall back to bare name only if qualified does not
                // resolve (today's member-nav penalty — the fix #1 target).
                (int qualifiedTotal, int qualifiedBytes) = Find(qualifiedQuery);
                int lookupBytes;
                bool qualifiedResolvesToday;
                if (qualifiedTotal > 0)
                {
                    lookupBytes = qualifiedBytes;
                    qualifiedResolvesToday = true;
                }
                else
                {
                    (int _, int simpleBytes) = Find(name);
                    lookupBytes = qualifiedBytes + simpleBytes;
                    qualifiedResolvesToday = false;
                }

                IndexedReferenceRow[] references = referencesByTarget[symbol.StableKey].Take(500).ToArray();
                // Mirror the MCP tool's DEFAULT lean reference shape (drops ProjectPath + FileContentHash) so fix #2
                // (responseShape="lean") is reflected. The tool returns this projection by default now.
                LeanReferenceRow[] leanReferences = references.Select(ToLeanReference).ToArray();
                int referenceBytes = JsonSerializer.Serialize(leanReferences, json).Length;
                int mcpBytes = lookupBytes + referenceBytes;

                if (!grepCache.TryGetValue(name, out GrepCost grep))
                {
                    grep = MeasureGrep(name, sourceFiles);
                    grepCache[name] = grep;
                }

                long grepMinBytes = grep.ContextBytes;
                long grepFullBytes = grep.PlainBytes + grep.FilesTotalBytes;
                double mcpTokens = mcpBytes / 4.0;
                double grepMinTokens = grepMinBytes / 4.0;
                double grepFullTokens = grepFullBytes / 4.0;
                double fullRatio = mcpTokens > 0 ? grepFullTokens / mcpTokens : 0;
                double minRatio = mcpTokens > 0 ? grepMinTokens / mcpTokens : 0;

                sumMcp += mcpBytes;
                sumGrepMin += grepMinBytes;
                sumGrepFull += grepFullBytes;
                total++;

                // A zero-hit grep is a non-answer, not a cheaper answer: its 0-byte "cost" can never be a fair win
                // (most common for constructors/indexers/special names that no word-boundary search can match). When
                // grep found nothing, MCP is the only arm that answered the "definition + all references" question, so
                // it counts as an MCP win regardless of byte comparison.
                bool grepAnswered = grep.Hits > 0;
                if (!grepAnswered)
                {
                    grepZeroHit++;
                }

                if (!grepAnswered || grepFullTokens > mcpTokens)
                {
                    winFull++;
                }

                if (!grepAnswered || grepMinTokens > mcpTokens)
                {
                    winMin++;
                }

                if (isMember)
                {
                    memberCount++;
                    if (!qualifiedResolvesToday)
                    {
                        memberQualifiedZeroToday++;
                    }
                }

                fullRatios.Add(fullRatio);
                worst.Add((name, grepFullBytes, mcpBytes));

                csv.Append('"').Append(name.Replace("\"", "\"\"")).Append('"').Append(',')
                    .Append(symbol.Kind).Append(',')
                    .Append(references.Length.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(mcpBytes.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(mcpTokens.ToString("F0", CultureInfo.InvariantCulture)).Append(',')
                    .Append(grepMinBytes.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(grepMinTokens.ToString("F0", CultureInfo.InvariantCulture)).Append(',')
                    .Append(grepFullBytes.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(grepFullTokens.ToString("F0", CultureInfo.InvariantCulture)).Append(',')
                    .Append(grep.FileCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(fullRatio.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                    .Append(minRatio.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                    .Append(isMember ? "1" : "0").Append(',')
                    .Append(qualifiedResolvesToday ? "1" : "0")
                    .AppendLine();
            }
            catch (Exception ex)
            {
                errors++;
                if (errors <= 20)
                {
                    csv.Append('"').Append(name.Replace("\"", "\"\"")).Append("\",ERROR,,,,,,,,,,,,\"")
                        .Append(ex.Message.Replace("\"", "'").Replace("\n", " ")).Append('"').AppendLine();
                }
            }

            if (total % 250 == 0)
            {
                Console.WriteLine($"[token-bench] {total} symbols measured ({stopwatch.Elapsed:mm\\:ss})...");
            }
        }

        stopwatch.Stop();

        fullRatios.Sort();
        double medianFull = fullRatios.Count > 0 ? fullRatios[fullRatios.Count / 2] : 0;
        double aggregateFull = sumMcp > 0 ? (double)sumGrepFull / sumMcp : 0;
        double aggregateMin = sumMcp > 0 ? (double)sumGrepMin / sumMcp : 0;
        worst.Sort((a, b) => b.GrepFull.CompareTo(a.GrepFull));

        StringBuilder summary = new();
        summary.AppendLine("# MCP index vs grep+read — FULL COVERAGE token baseline");
        summary.AppendLine($"target solution: {solutionPath}");
        summary.AppendLine($"elapsed: {stopwatch.Elapsed:hh\\:mm\\:ss}");
        summary.AppendLine($"symbols measured: {total}   (errors: {errors})");
        summary.AppendLine($"references in index: {allReferences.Count}");
        summary.AppendLine("metric: context bytes an agent ingests to answer 'definition + all references', tokens = bytes / 4");
        summary.AppendLine();
        summary.AppendLine("## Aggregate");
        summary.AppendLine($"MCP total tokens:        {sumMcp / 4:N0}");
        summary.AppendLine($"grep-min total tokens:   {sumGrepMin / 4:N0}   (rg -C3 candidate view; not a real answer)");
        summary.AppendLine($"grep-full total tokens:  {sumGrepFull / 4:N0}   (rg matches + reading every hit file)");
        summary.AppendLine($"AGGREGATE RATIO  grep-full / MCP = {aggregateFull:F2}x   <-- headline");
        summary.AppendLine($"AGGREGATE RATIO  grep-min  / MCP = {aggregateMin:F2}x");
        summary.AppendLine();
        summary.AppendLine("## Per-symbol");
        summary.AppendLine($"median full ratio:       {medianFull:F2}x");
        summary.AppendLine($"MCP wins vs grep-full on: {winFull}/{total} ({(total > 0 ? 100.0 * winFull / total : 0):F1}%)");
        summary.AppendLine($"MCP wins vs grep-min  on: {winMin}/{total} ({(total > 0 ? 100.0 * winMin / total : 0):F1}%)");
        summary.AppendLine($"  (includes {grepZeroHit} symbols where grep returned 0 hits — a non-answer, e.g. constructors/indexers/special names — counted as MCP wins since grep could not answer at all)");
        summary.AppendLine();
        summary.AppendLine("## Qualified Type.Member lookup");
        summary.AppendLine($"members measured: {memberCount}");
        summary.AppendLine($"members whose qualified 'Type.Member' query still returns 0 (forced homonym fallback): {memberQualifiedZeroToday}");
        summary.AppendLine("  -> lower is better; remaining misses identify nested type or matching edge cases.");
        summary.AppendLine();
        summary.AppendLine("## Top 15 by grep-full cost (where the index saves most)");
        foreach ((string Name, long GrepFull, long Mcp) entry in worst.Take(15))
        {
            double r = entry.Mcp > 0 ? (double)entry.GrepFull / entry.Mcp : 0;
            summary.AppendLine($"  {entry.Name,-45} grep-full {entry.GrepFull / 4,9:N0} tok  vs MCP {entry.Mcp / 4,7:N0} tok  = {r:F1}x");
        }

        string csvPath = Path.Combine(outputDir, "baseline.csv");
        string summaryPath = Path.Combine(outputDir, "baseline-summary.txt");
        File.WriteAllText(csvPath, csv.ToString());
        File.WriteAllText(summaryPath, summary.ToString());
        Console.WriteLine(summary.ToString());
        Console.WriteLine($"[token-bench] wrote {csvPath} and {summaryPath}");

        Assert.True(total > 0, "No symbols measured.");
    }

    // Mirrors AIMonitor.McpServer's AIMonitorIndexedReferenceResult (the default "lean" find_indexed_references shape).
    private sealed record LeanReferenceRow(
        string TargetStableKey,
        string FilePath,
        int Line,
        int Column,
        string ReferenceKind,
        string Snippet,
        string TargetName,
        string TargetKind,
        string CallerStableKey,
        string CallerName,
        string CallerKind);

    private static LeanReferenceRow ToLeanReference(IndexedReferenceRow reference)
    {
        return new LeanReferenceRow(
            reference.TargetStableKey,
            reference.FilePath,
            reference.Line,
            reference.Column,
            reference.ReferenceKind,
            reference.Snippet,
            reference.TargetName,
            reference.TargetKind,
            reference.CallerStableKey,
            reference.CallerName,
            reference.CallerKind);
    }

    private sealed record SourceFile(string AbsPath, string[] Lines, long Size);

    private readonly record struct GrepCost(long PlainBytes, long ContextBytes, int FileCount, long FilesTotalBytes, int Hits);

    private static List<SourceFile> LoadSourceFiles(string root)
    {
        List<SourceFile> files = new();
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string normalized = path.Replace('/', '\\');
            if (normalized.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("\\.vs\\", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".bak" || ext == ".log")
            {
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch
            {
                continue;
            }

            int sniff = Math.Min(bytes.Length, 8000);
            bool binary = false;
            for (int index = 0; index < sniff; index++)
            {
                if (bytes[index] == 0)
                {
                    binary = true;
                    break;
                }
            }

            if (binary)
            {
                continue;
            }

            string text = Encoding.UTF8.GetString(bytes);
            files.Add(new SourceFile(path, text.Split('\n'), bytes.Length));
        }

        return files;
    }

    private static GrepCost MeasureGrep(string name, List<SourceFile> files)
    {
        Regex word = new(@"\b" + Regex.Escape(name) + @"\b", RegexOptions.CultureInvariant);
        long plainBytes = 0;
        long contextBytes = 0;
        long filesTotalBytes = 0;
        int fileCount = 0;
        int hits = 0;

        foreach (SourceFile file in files)
        {
            List<int>? matches = null;
            for (int i = 0; i < file.Lines.Length; i++)
            {
                string line = file.Lines[i];
                if (!line.Contains(name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!word.IsMatch(line))
                {
                    continue;
                }

                matches ??= new List<int>();
                matches.Add(i);
            }

            if (matches == null)
            {
                continue;
            }

            fileCount++;
            filesTotalBytes += file.Size;
            hits += matches.Count;

            // plain `rg -n` output: one "path:line:text" per match line
            foreach (int i in matches)
            {
                string line = file.Lines[i].TrimEnd('\r');
                plainBytes += Encoding.UTF8.GetByteCount(file.AbsPath) + Encoding.UTF8.GetByteCount(line)
                    + EncodedIntLength(i + 1) + 3; // two ':' separators + '\n'
            }

            // `rg -C3` context: union of match +/- 3 lines, merged, with "--" separators between runs
            SortedSet<int> included = new();
            foreach (int match in matches)
            {
                int from = Math.Max(0, match - 3);
                int to = Math.Min(file.Lines.Length - 1, match + 3);
                for (int j = from; j <= to; j++)
                {
                    included.Add(j);
                }
            }

            int previous = int.MinValue;
            foreach (int idx in included)
            {
                if (previous != int.MinValue && idx != previous + 1)
                {
                    contextBytes += 3; // "--\n"
                }

                string line = file.Lines[idx].TrimEnd('\r');
                contextBytes += Encoding.UTF8.GetByteCount(file.AbsPath) + Encoding.UTF8.GetByteCount(line)
                    + EncodedIntLength(idx + 1) + 3;
                previous = idx;
            }
        }

        return new GrepCost(plainBytes, contextBytes, fileCount, filesTotalBytes, hits);
    }

    private static int EncodedIntLength(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture).Length;
    }
}
