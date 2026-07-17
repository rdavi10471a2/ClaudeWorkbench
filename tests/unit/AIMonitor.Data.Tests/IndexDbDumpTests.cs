using System.Linq;

namespace AIMonitor.Data.Tests;

// Ad-hoc diagnostic: dump the reference-kind histogram of arbitrary index DBs via SolutionIndexProbe, to compare what
// the live host-built index contains vs a fresh rebuild — specifically hunting for razor-generated:* rows.
// Set DUMP_DBS to a ';'-separated list of solution-index.sqlite paths. No-op if unset. Read-only (no EnsureCreated).
public sealed class IndexDbDumpTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper output;

    public IndexDbDumpTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    [Trait("Suite", "Probe")]
    public void Dump_reference_kinds_for_dbs()
    {
        string? paths = Environment.GetEnvironmentVariable("DUMP_DBS");
        if (string.IsNullOrWhiteSpace(paths))
        {
            return;
        }

        foreach (string path in paths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!File.Exists(path))
            {
                output.WriteLine($"MISSING: {path}");
                continue;
            }

            SolutionIndexProbe probe = new(new SolutionIndexDatabase(path));
            SolutionIndexCounts counts = probe.GetCounts();
            output.WriteLine($"DB {path}");
            output.WriteLine($"   projects={counts.Projects} symbols={counts.Symbols} references={counts.References} callSites={counts.CallSites} relationships={counts.Relationships}");
            output.WriteLine($"   crossProjectRefs={probe.GetCrossProjectReferenceCount()}");
            output.WriteLine($"   razor present={probe.HasReferenceKindPrefix("razor")}  razor-generated present={probe.HasReferenceKindPrefix("razor-generated")}");
            foreach (ReferenceKindCount kind in probe.GetReferenceKindCounts())
            {
                output.WriteLine($"      {kind.Kind} = {kind.Count}");
            }
        }
    }
}
