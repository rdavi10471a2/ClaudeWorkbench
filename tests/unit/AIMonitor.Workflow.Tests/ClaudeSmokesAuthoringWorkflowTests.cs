using System.Security.Cryptography;
using AIMonitor.Core;
using AIMonitor.Workflow;

namespace AIMonitor.Workflow.Tests;

// ClaudeSmokes — authoring a new Blazor PAGE as a 3-file unit through the safe-edit workflow. Authored by Claude
// (review+test role; no src/ or samples/ edits). LOCAL.
//
// Mean-teacher intent: a real page is markup + code-behind + scoped CSS, and the safe-edit contract must (1) couple all
// three under ONE page session, (2) leave the committed backing-store fixture byte-identical (new-file authoring never
// creates watched source), and (3) keep per-edit C# validation scoped to .cs ONLY — markup and scoped CSS submit
// without C# validation while a broken code-behind is rejected before it can ever be staged. These fail if the page
// session decouples, if authoring dirties the fixture, or if validation scope-creeps onto non-C# assets (or stops
// guarding the .cs code-behind).
public sealed class ClaudeSmokesAuthoringWorkflowTests
{
	[Fact]
	[Trait("Suite", "ClaudeSmokes")]
	public void ClaudeSmokes_authoring_a_three_file_page_couples_under_one_session_and_never_touches_backing_store()
	{
		string sampleRoot = Path.Combine(FindRepositoryRoot(), "samples", "watched-solutions", "BlazorSample");
		// A KNOWN committed backing-store file — authoring a brand-new page must never dirty an existing fixture file.
		string backingStoreFile = Path.Combine(sampleRoot, "Components", "CustomerList.razor.cs");
		string backingHashBefore = Sha256(backingStoreFile);

		string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorClaudeSmokesAuthoring", Guid.NewGuid().ToString("N"));
		string workCopyRoot = Path.Combine(tempRoot, "BlazorSample");
		CopyTree(sampleRoot, workCopyRoot);

		string copyProject = Path.Combine(workCopyRoot, "BlazorSample.csproj");
		MonitorSettings settings = MonitorSettings.Create(
			Path.Combine(tempRoot, "_repo"), copyProject, Path.Combine(tempRoot, "_runtime"));
		WorkflowEditService service = new(settings);

		// The 3 FUTURE watched files of the new page — none exist in the materialized copy yet.
		string razorPath = Path.Combine(workCopyRoot, "Components", "OrderList.razor");
		string codeBehindPath = Path.Combine(workCopyRoot, "Components", "OrderList.razor.cs");
		string scopedCssPath = Path.Combine(workCopyRoot, "Components", "OrderList.razor.css");

		// Authored content — each file carries a distinct marker so we can prove the staged candidate is the authored one.
		string razorContent =
			"@page \"/orders\"\r\n" +
			"<h3>OrderList-MARKER</h3>\r\n" +
			"<input @bind=\"Filter\" />\r\n" +
			"<button @onclick=\"LoadAsync\">Load</button>\r\n" +
			"@code {\r\n" +
			"\tprivate BlazorSample.Repositories.CustomerService Service { get; } = new BlazorSample.Repositories.CustomerService();\r\n" +
			"\tpublic string Filter { get; set; } = \"\";\r\n" +
			"\tprivate async Task LoadAsync()\r\n" +
			"\t{\r\n" +
			"\t\tBlazorSample.Model.Customer? loaded = await Service.LoadAsync(1);\r\n" +
			"\t\tFilter = loaded?.Name ?? \"\";\r\n" +
			"\t}\r\n" +
			"}\r\n";
		string codeBehindContent =
			"namespace BlazorSample.Components;\r\n" +
			"\r\n" +
			"public partial class OrderList\r\n" +
			"{\r\n" +
			"\t// OrderList-MARKER code-behind.\r\n" +
			"\tpublic int LoadedCount { get; private set; }\r\n" +
			"\r\n" +
			"\tprivate void RecordLoad()\r\n" +
			"\t{\r\n" +
			"\t\tLoadedCount++;\r\n" +
			"\t}\r\n" +
			"}\r\n";
		string scopedCssContent =
			"/* OrderList-MARKER scoped css */\r\n" +
			"h3 {\r\n" +
			"\tcolor: rebeccapurple;\r\n" +
			"}\r\n";

		// ONE shared session id couples the three files into a single coupled page edit.
		string sharedSessionId = "order-page-" + Guid.NewGuid().ToString("N");

		service.NewFile(razorPath);
		service.SubmitFile(razorPath, razorContent);
		StagedEditRecord stagedRazor = service.Stage(razorPath, "order page", sharedSessionId);

		service.NewFile(codeBehindPath);
		service.SubmitFile(codeBehindPath, codeBehindContent);
		StagedEditRecord stagedCodeBehind = service.Stage(codeBehindPath, "order page", sharedSessionId);

		service.NewFile(scopedCssPath);
		service.SubmitFile(scopedCssPath, scopedCssContent);
		StagedEditRecord stagedScopedCss = service.Stage(scopedCssPath, "order page", sharedSessionId);

		// Each staged candidate carries the authored marker (the workflow staged the authored content, not an empty shell).
		Assert.Contains("OrderList-MARKER", File.ReadAllText(stagedRazor.StagedFilePath), StringComparison.Ordinal);
		Assert.Contains("OrderList-MARKER", File.ReadAllText(stagedCodeBehind.StagedFilePath), StringComparison.Ordinal);
		Assert.Contains("OrderList-MARKER", File.ReadAllText(stagedScopedCss.StagedFilePath), StringComparison.Ordinal);
		// ...and the markup/code-behind specifically carry their own distinct authored shapes (specific, not generic).
		Assert.Contains("@page \"/orders\"", File.ReadAllText(stagedRazor.StagedFilePath), StringComparison.Ordinal);
		Assert.Contains("public partial class OrderList", File.ReadAllText(stagedCodeBehind.StagedFilePath), StringComparison.Ordinal);

		// COUPLING: all three staged records report the SAME shared session id — one coupled page session.
		Assert.Equal(sharedSessionId, stagedRazor.SessionId);
		Assert.Equal(sharedSessionId, stagedCodeBehind.SessionId);
		Assert.Equal(sharedSessionId, stagedScopedCss.SessionId);
		// ...and they are three DISTINCT staged records (the coupling did not collapse them into one).
		Assert.NotEqual(stagedRazor.StagedRecordId, stagedCodeBehind.StagedRecordId);
		Assert.NotEqual(stagedRazor.StagedRecordId, stagedScopedCss.StagedRecordId);
		Assert.NotEqual(stagedCodeBehind.StagedRecordId, stagedScopedCss.StagedRecordId);

		// NEW-FILE authoring did NOT create watched source — the operator still owns creation via WinMerge.
		Assert.False(File.Exists(razorPath));
		Assert.False(File.Exists(codeBehindPath));
		Assert.False(File.Exists(scopedCssPath));

		// REGRESSION/ISOLATION: the existing committed page fixture is byte-identical — authoring a NEW page never
		// dirtied the existing CustomerList backing store (repeatable smoke).
		Assert.Equal(backingHashBefore, Sha256(backingStoreFile));
	}

	[Fact]
	[Trait("Suite", "ClaudeSmokes")]
	public void ClaudeSmokes_coupled_page_edit_rejects_broken_code_behind_but_not_markup_or_css()
	{
		string sampleRoot = Path.Combine(FindRepositoryRoot(), "samples", "watched-solutions", "BlazorSample");
		string tempRoot = Path.Combine(Path.GetTempPath(), "AIMonitorClaudeSmokesAuthoringScope", Guid.NewGuid().ToString("N"));
		string workCopyRoot = Path.Combine(tempRoot, "BlazorSample");
		CopyTree(sampleRoot, workCopyRoot);

		string copyProject = Path.Combine(workCopyRoot, "BlazorSample.csproj");
		MonitorSettings settings = MonitorSettings.Create(
			Path.Combine(tempRoot, "_repo"), copyProject, Path.Combine(tempRoot, "_runtime"));
		WorkflowEditService service = new(settings);

		string razorPath = Path.Combine(workCopyRoot, "Components", "OrderList.razor");
		string scopedCssPath = Path.Combine(workCopyRoot, "Components", "OrderList.razor.css");
		string codeBehindPath = Path.Combine(workCopyRoot, "Components", "OrderList.razor.cs");

		// Valid markup (.razor) — non-.cs, so C# validation is SKIPPED and submit SUCCEEDS.
		string razorContent =
			"@page \"/orders\"\r\n" +
			"<h3>OrderList</h3>\r\n" +
			"<input @bind=\"Filter\" />\r\n" +
			"@code {\r\n" +
			"\tpublic string Filter { get; set; } = \"\";\r\n" +
			"}\r\n";
		service.NewFile(razorPath);
		EditSessionStatus razorStatus = service.SubmitFile(razorPath, razorContent);
		Assert.Contains("OrderList", File.ReadAllText(razorStatus.WorkingFilePath), StringComparison.Ordinal);

		// Valid scoped CSS (.razor.css) — non-.cs text asset, C# validation SKIPPED and submit SUCCEEDS.
		string scopedCssContent = "h3 { color: rebeccapurple; }\r\n";
		service.NewFile(scopedCssPath);
		EditSessionStatus cssStatus = service.SubmitFile(scopedCssPath, scopedCssContent);
		Assert.Contains("rebeccapurple", File.ReadAllText(cssStatus.WorkingFilePath), StringComparison.Ordinal);

		// Broken code-behind (.razor.cs) — C# SYNTAX ERROR. SubmitFile must THROW (per-edit C# validation is scoped to
		// .cs and rejects the broken candidate BEFORE it can be staged).
		string brokenCodeBehind =
			"namespace BlazorSample.Components; public partial class OrderList { public void Bad( }";
		service.NewFile(codeBehindPath);
		Assert.ThrowsAny<Exception>(() => service.SubmitFile(codeBehindPath, brokenCodeBehind));

		// NEGATIVE: the rejected code-behind was never authored into watched source, and the broken candidate did not
		// land a staged record we could merge — the bad .cs is blocked at the edge, the markup/css are not.
		Assert.False(File.Exists(codeBehindPath));
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

	private static string Sha256(string path)
	{
		using FileStream stream = File.OpenRead(path);
		return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
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
