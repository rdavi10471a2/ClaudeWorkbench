using System.Collections.Concurrent;
using AIMonitor.Core;

namespace AIMonitor.Core.Tests;

public sealed class CompileIndexTraceTests
{
    private static MonitorSettings NewSettingsWithTempRuntime(out string runtimeRoot)
    {
        string root = Path.Combine(Path.GetTempPath(), "AIMonitorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        runtimeRoot = Path.Combine(root, "runtime");
        return MonitorSettings.Create(root, Path.Combine(root, "App.slnx"), runtimeRoot);
    }

    [Fact]
    public void Trace_captures_the_acted_on_paths_and_preserves_event_order()
    {
        MonitorSettings settings = NewSettingsWithTempRuntime(out string runtimeRoot);
        string gatePath = Path.Combine(runtimeRoot, "watched-solutions", "App", "validation-workspace");
        string indexPath = settings.WatchedSolutionPath;

        // The exact two-compile shape we are proving: the gate builds the mirror, THEN the index compiles
        // the real tree. Recorded in that order.
        CompileIndexTrace.Record(settings, "gate-build.start", gatePath, "out-of-proc dotnet build on the mirror");
        CompileIndexTrace.Record(settings, "gate-build.done", gatePath, "exit=0");
        CompileIndexTrace.Record(settings, "index-compile.start", indexPath, "in-proc MSBuildWorkspace open");
        CompileIndexTrace.Record(settings, "index-compile.done", indexPath, "in-proc compile ms=1");

        string traceFile = CompileIndexTrace.GetTraceFilePath(settings);
        Assert.True(File.Exists(traceFile), "trace file should exist under the watched-solution workspace");
        Assert.StartsWith(Path.GetFullPath(runtimeRoot), Path.GetFullPath(traceFile));

        string[] lines = File.ReadAllLines(traceFile);

        // Fact 1: each event names the PATH it acted on — the gate names the mirror, the index names the real tree.
        Assert.Contains(lines, line => line.Contains("gate-build.start") && line.Contains(gatePath));
        Assert.Contains(lines, line => line.Contains("index-compile.start") && line.Contains(indexPath));

        // Fact 2: the file preserves ORDER, so "the index runs after the compile" is provable by reading it —
        // the gate build completes before the index compile starts.
        int gateDone = Array.FindIndex(lines, line => line.Contains("gate-build.done"));
        int indexStart = Array.FindIndex(lines, line => line.Contains("index-compile.start"));
        Assert.InRange(gateDone, 0, int.MaxValue);
        Assert.InRange(indexStart, 0, int.MaxValue);
        Assert.True(gateDone < indexStart, "gate-build.done must appear before index-compile.start");

        // Sequence numbers are monotonic increasing across the recorded events.
        int[] sequences = lines
            .Select(line => int.Parse(line.Substring(1, line.IndexOf(']') - 1)))
            .ToArray();
        for (int i = 1; i < sequences.Length; i++)
        {
            Assert.True(sequences[i] > sequences[i - 1], "sequence numbers must strictly increase");
        }
    }

    [Fact]
    public void Echo_sink_receives_each_event_and_a_throwing_sink_never_breaks_recording()
    {
        MonitorSettings settings = NewSettingsWithTempRuntime(out _);
        ConcurrentQueue<string> echoed = new();
        Action<string>? previous = CompileIndexTrace.Echo;
        try
        {
            // A sink that throws must not propagate — recording is best-effort for the caller (a build/index).
            CompileIndexTrace.Echo = line =>
            {
                echoed.Enqueue(line);
                throw new InvalidOperationException("sink blows up");
            };

            Exception? captured = Record.Exception(() =>
                CompileIndexTrace.Record(settings, "index-compile.start", settings.WatchedSolutionPath, "detail"));

            Assert.Null(captured);
            Assert.Contains(echoed, line => line.Contains("index-compile.start") && line.Contains("detail"));
        }
        finally
        {
            CompileIndexTrace.Echo = previous;
        }
    }
}
