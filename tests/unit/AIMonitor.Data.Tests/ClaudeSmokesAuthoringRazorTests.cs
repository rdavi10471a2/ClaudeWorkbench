using AIMonitor.MSBuild;

namespace AIMonitor.Data.Tests;

// ClaudeSmokes — AUTHORING a new Blazor PAGE as a 3-file unit (.razor + .razor.cs + .razor.css) over a NEW
// repository, then proving the index handles it. Authored by Claude (review+test role; no src/ or samples/ edits).
// LOCAL. Self-contained: MATERIALIZE a temp copy of the in-repo BlazorSample (skipping bin/obj), write the NEW
// Order-* source into the COPY only, then index the COPY. The checked-in sample is never mutated.
//
// Mean-teacher intent: this is NOT a happy-path "a row exists" smoke. It pins exact identities and bakes in the
// known extraction gotchas, then guards three failure modes that a naive author would miss:
//   * REGRESSION — augmenting the project with an OrderList page must not drop the existing CustomerList razor refs.
//   * ISOLATION  — the new OrderList razor ref must map to OrderList.razor, distinct from CustomerList.razor.
//   * NEGATIVE   — the scoped .razor.css is a TEXT ASSET: it must yield NO index symbols (no FilePath ends ".css").
// It also encodes the defensible Razor boundary: only the .razor @code C# references map back (razor:* refs); the
// markup @bind / @onclick attribute bindings are deliberately NOT asserted (documented Razor limit).
public sealed class ClaudeSmokesAuthoringRazorTests
{
	[Fact]
	[Trait("Suite", "ClaudeSmokes")]
	public async Task ClaudeSmokes_authoring_blazor_page_three_file_unit_indexes_without_dropping_existing_page()
	{
		string sampleRoot = Path.Combine(FindRepositoryRoot(), "samples", "watched-solutions", "BlazorSample");
		Assert.True(File.Exists(Path.Combine(sampleRoot, "BlazorSample.csproj")), $"In-repo Blazor sample missing under {sampleRoot}.");

		// MATERIALIZE a pristine copy; all new files land in the COPY, never the checked-in backing store.
		string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorClaudeSmokesAuthorRazor", Guid.NewGuid().ToString("N"));
		string copyRoot = Path.Combine(tempRoot, "BlazorSample");
		CopyTree(sampleRoot, copyRoot);

		// NEW repository trio + service. Gotcha #2: explicit ctor on the entity, and the only 'new' we intend to
		// observe lives in a METHOD BODY (CreateDefault), not a field initializer.
		await File.WriteAllTextAsync(Path.Combine(copyRoot, "Model", "Order.cs"), """
			namespace BlazorSample.Model;

			public sealed class Order
			{
				public Order() { }
				public int Id { get; set; }
				public string Name { get; set; } = "";
			}
			""");
		await File.WriteAllTextAsync(Path.Combine(copyRoot, "Repositories", "IOrderRepository.cs"), """
			using BlazorSample.Model;

			namespace BlazorSample.Repositories;

			public interface IOrderRepository
			{
				Task<Order?> GetByIdAsync(int id);
			}
			""");
		await File.WriteAllTextAsync(Path.Combine(copyRoot, "Repositories", "OrderRepository.cs"), """
			using BlazorSample.Model;

			namespace BlazorSample.Repositories;

			public sealed class OrderRepository : RepositoryBase<Order>, IOrderRepository
			{
				public override Task<Order?> GetByIdAsync(int id)
				{
					Log("get-order");
					return base.GetByIdAsync(id);
				}
			}
			""");
		await File.WriteAllTextAsync(Path.Combine(copyRoot, "Repositories", "OrderService.cs"), """
			using BlazorSample.Model;

			namespace BlazorSample.Repositories;

			public sealed class OrderService
			{
				private readonly OrderRepository repository = new OrderRepository();

				public OrderService() { }

				public Task<Order?> LoadAsync(int id)
				{
					return repository.GetByIdAsync(id);
				}

				public Order CreateDefault()
				{
					Order order = new Order();
					return order;
				}
			}
			""");

		// NEW 3-file page unit over the new repository. .razor @code references map back; markup @bind/@onclick do not.
		await File.WriteAllTextAsync(Path.Combine(copyRoot, "Components", "OrderList.razor"), """
			<h3>Orders</h3>
			<input @bind="Filter" />
			<button @onclick="LoadAsync">Load</button>
			@code {
				private OrderService Service { get; } = new OrderService();
				public string Filter { get; set; } = "";
				private async Task LoadAsync()
				{
					Order? loaded = await Service.LoadAsync(1);
					Filter = loaded?.Name ?? "";
				}
			}
			""");
		await File.WriteAllTextAsync(Path.Combine(copyRoot, "Components", "OrderList.razor.cs"), """
			namespace BlazorSample.Components;

			public partial class OrderList
			{
				public int LoadedCount { get; private set; }
				private void RecordLoad() => LoadedCount++;
			}
			""");
		// Scoped CSS — a TEXT ASSET. Yields NO index symbols (negative-assertion material).
		await File.WriteAllTextAsync(Path.Combine(copyRoot, "Components", "OrderList.razor.css"), """
			h3 { color: teal; }
			button { cursor: pointer; }
			""");

		// INDEX the copy.
		MSBuildSolutionSnapshot snapshot = await new MSBuildWorkspaceLoader().OpenProjectAsync(Path.Combine(copyRoot, "BlazorSample.csproj"));
		string databasePath = Path.Combine(tempRoot, "index.sqlite");
		SolutionIndexStore store = new(new SolutionIndexDatabase(databasePath));
		store.SaveSnapshot(snapshot);

		System.Collections.Generic.IReadOnlyList<IndexedSymbolRow> symbols = store.ListSymbols();
		const string orderRepoType = "BlazorSample.Repositories.OrderRepository";

		// (1) Code-behind class + its method are indexed; ContainingType is fully-qualified and ends with OrderList.
		Assert.Contains(symbols, s => s.Name == "OrderList" && s.Kind == "NamedType");
		IndexedSymbolRow recordLoad = Assert.Single(symbols, s => s.Name == "RecordLoad" && s.ContainingType.EndsWith("OrderList", StringComparison.Ordinal));
		Assert.Equal("BlazorSample.Components.OrderList", recordLoad.ContainingType);

		// (2) Order repository graph: inherits_from + implements_interface_member. Gotcha #1: NO 'overrides' row is
		// emitted for an override of the GENERIC base virtual (RepositoryBase<T>.GetByIdAsync) — do not assert it.
		string orderRepoGetById = Assert.Single(symbols, s => s.Name == "GetByIdAsync" && s.ContainingType == orderRepoType).StableKey;
		System.Collections.Generic.IReadOnlyList<IndexedRelationshipRow> relationships = store.ListRelationships();
		Assert.Contains(relationships, r => r.RelationshipKind == "inherits_from" && r.SourceName == "OrderRepository" && r.TargetName == "RepositoryBase");
		Assert.Contains(relationships, r => r.RelationshipKind == "implements_interface_member" && r.SourceName == "GetByIdAsync"
			&& r.SourceStableKey == orderRepoGetById);
		Assert.DoesNotContain(relationships, r => r.RelationshipKind == "overrides" && r.SourceStableKey == orderRepoGetById);

		// (3) Razor @code references map back to the .razor source (defensible boundary; prefix "razor", per extractor
		//     MSBuildWorkspaceLoader.cs ~L744: $"razor:{node.Kind()}").
		System.Collections.Generic.IReadOnlyList<IndexedReferenceRow> references = store.ListReferences();
		IndexedReferenceRow[] orderRazorRefs = references
			.Where(r => r.ReferenceKind.StartsWith("razor", StringComparison.OrdinalIgnoreCase)
				&& r.FilePath.EndsWith("OrderList.razor", StringComparison.OrdinalIgnoreCase))
			.ToArray();
		Assert.NotEmpty(orderRazorRefs);
		// The @code body's call into the new service maps back as a razor:* reference to the .razor source.
		Assert.Contains(orderRazorRefs, r => r.ReferenceKind == "razor:InvocationExpression" && r.TargetName == "LoadAsync");
		IndexedReferenceRow orderRazorRef = orderRazorRefs[0];

		// (4) REGRESSION: augmenting the project did not drop the EXISTING CustomerList page's razor refs.
		Assert.Contains(references, r => r.ReferenceKind.StartsWith("razor", StringComparison.OrdinalIgnoreCase)
			&& r.FilePath.EndsWith("CustomerList.razor", StringComparison.OrdinalIgnoreCase));

		// (5) ISOLATION: the new page's razor ref maps to OrderList.razor, distinct from CustomerList.razor — the
		//     two pages do not bleed into one another.
		Assert.EndsWith("OrderList.razor", orderRazorRef.FilePath, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("CustomerList.razor", orderRazorRef.FilePath, StringComparison.OrdinalIgnoreCase);

		// (6) NEGATIVE: scoped .razor.css is a text asset — it contributes NO index symbols, and a never-authored
		//     symbol name is absent.
		Assert.DoesNotContain(symbols, s => s.FilePath.EndsWith(".css", StringComparison.OrdinalIgnoreCase));
		Assert.DoesNotContain(symbols, s => s.Name == "OrderManager");
	}

	private static void CopyTree(string source, string destination)
	{
		foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
		{
			string name = Path.GetFileName(dir);
			if (name is "bin" or "obj")
			{
				continue;
			}

			Directory.CreateDirectory(dir.Replace(source, destination, StringComparison.Ordinal));
		}

		Directory.CreateDirectory(destination);
		foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
		{
			if (file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj"))
			{
				continue;
			}

			string target = file.Replace(source, destination, StringComparison.Ordinal);
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.Copy(file, target, overwrite: true);
		}
	}

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "ClaudeWorkbench.slnx")))
			{
				return directory.FullName;
			}

			directory = directory.Parent;
		}

		throw new DirectoryNotFoundException("Could not find ClaudeWorkbench.slnx.");
	}
}
