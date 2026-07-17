using AIMonitor.MSBuild;

namespace AIMonitor.Data.Tests;

// ClaudeSmokes — Phase 1 CMB-parity CI gate, authored by Claude (review+test role; no production edits).
//
// Why this exists: the existing Phase-1 unit/integration tests HAND-SEED MSBuildReferenceSnapshot rows and
// then assert they round-trip through the store/query/MCP. That proves the Data/query layer but BYPASSES the
// real extractor — those tests would still pass if MSBuildWorkspaceLoader relationship/call-site extraction
// regressed to a stub. These ClaudeSmokes drive the REAL loader end-to-end against a hermetic fixture and
// assert CONTENT (real kinds + real caller identity), so they FAIL if extraction is faked.
public sealed class ClaudeSmokesPhase1IndexTests
{
    [Fact]
    [Trait("Suite", "ClaudeSmokes")]
    public async Task ClaudeSmokes_real_extraction_emits_relationships_callsites_and_resolves_real_caller_identity()
    {
        string root = Path.Combine(Path.GetTempPath(), "AIMonitorClaudeSmokes", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        string projectPath = Path.Combine(root, "Domain.csproj");
        await File.WriteAllTextAsync(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);

        // Representative C# shapes covering every Phase-1 relationship/call kind.
        await File.WriteAllTextAsync(Path.Combine(root, "Domain.cs"), """
            namespace ClaudeSmokesFixture;

            public interface IFoo
            {
                void Run();
            }

            public class Base
            {
                public virtual void M() { }
            }

            public partial class Derived : Base, IFoo
            {
                public override void M() { }

                public void Run() { }

                public void CallSite()
                {
                    Run();
                    Widget widget = new Widget();
                }
            }

            public sealed class Widget
            {
                public Widget() { }
            }
            """);

        // Second file makes Derived a partial type across files (partial_declaration relationship).
        await File.WriteAllTextAsync(Path.Combine(root, "DomainPart.cs"), """
            namespace ClaudeSmokesFixture;

            public partial class Derived
            {
                public void Extra() { }
            }
            """);

        MSBuildSolutionSnapshot snapshot = await new MSBuildWorkspaceLoader().OpenProjectAsync(projectPath);

        // (A) EXTRACTION must emit the real relationship + call kinds. This is the layer the seeded tests bypass:
        // if AddRelationshipReferences / call-site extraction regress to a stub, these assertions fail.
        string[] referenceKinds = snapshot.Projects
            .SelectMany(project => project.References)
            .Select(reference => reference.ReferenceKind)
            .ToArray();
        Assert.Contains("inherits_from", referenceKinds);
        Assert.Contains("overrides", referenceKinds);
        Assert.Contains("implements_interface_member", referenceKinds);
        Assert.Contains("partial_declaration", referenceKinds);
        Assert.Contains("InvocationExpression", referenceKinds);
        Assert.Contains("ObjectCreationExpression", referenceKinds);

        // (B) DERIVATION on REAL extracted rows: persist via the production store, then assert the derived
        // call_sites carry real caller identity resolved by FindContainingSymbol (not a pre-seeded value).
        string databasePath = Path.Combine(root, "index.sqlite");
        SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));
        store.SaveSnapshot(snapshot);

        System.Collections.Generic.IReadOnlyList<IndexedSymbolRow> symbols = store.ListSymbols();
        const string derivedType = "ClaudeSmokesFixture.Derived";
        string derivedRunKey = symbols
            .Single(symbol => symbol.Name == "Run" && symbol.ContainingType == derivedType).StableKey;
        string callSiteKey = symbols
            .Single(symbol => symbol.Name == "CallSite" && symbol.ContainingType == derivedType).StableKey;

        System.Collections.Generic.IReadOnlyList<IndexedCallSiteRow> callSites = store.ListCallSites();
        IndexedCallSiteRow invocation = Assert.Single(
            callSites,
            site => site.CallKind == "InvocationExpression" && site.CallerName == "CallSite");
        Assert.Equal(derivedRunKey, invocation.TargetStableKey);   // call resolved to the real target symbol
        Assert.Equal(callSiteKey, invocation.CallerStableKey);     // FindContainingSymbol resolved the REAL caller
        Assert.Equal("Method", invocation.CallerKind);
        Assert.Contains(
            callSites,
            site => site.CallKind == "ObjectCreationExpression" && site.CallerName == "CallSite");

        // Derived relationships table carries real source/target identities (not seeded kinds).
        System.Collections.Generic.IReadOnlyList<IndexedRelationshipRow> relationships = store.ListRelationships();
        Assert.Contains(relationships, relationship => relationship.RelationshipKind == "inherits_from");
        Assert.Contains(relationships, relationship => relationship.RelationshipKind == "overrides");
        Assert.Contains(relationships, relationship => relationship.RelationshipKind == "implements_interface_member");
        Assert.Contains(relationships, relationship => relationship.RelationshipKind == "partial_declaration");

        // (C) RICH references carry caller identity + file content hash on REAL-extracted rows.
        System.Collections.Generic.IReadOnlyList<IndexedReferenceRow> references = store.ListReferences(derivedRunKey);
        IndexedReferenceRow invocationReference = Assert.Single(
            references,
            reference => reference.ReferenceKind == "InvocationExpression");
        Assert.Equal("CallSite", invocationReference.CallerName);
        Assert.NotEqual(string.Empty, invocationReference.CallerStableKey);
        Assert.NotEqual(string.Empty, invocationReference.FileContentHash);
    }
}
