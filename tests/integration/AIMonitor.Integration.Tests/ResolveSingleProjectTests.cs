using AIMonitor.Core;

namespace AIMonitor.Integration.Tests;

// ADR-0007: the build-output read path rides a SINGLE project — and it should not matter whether the watched
// entry is a bare .csproj, a .slnx, or a legacy .sln. These pin the resolver: one project => ride the build
// (its .csproj path), more than one => null (fall back to the existing loader until multi-project read lands).
public sealed class ResolveSingleProjectTests
{
    [Fact]
    public void Bare_csproj_resolves_to_itself()
    {
        string dir = NewTempDir();
        string csproj = Path.Combine(dir, "Solo.csproj");
        File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Assert.Equal(csproj, WatchedSolutionInfo.ResolveSingleProject(csproj));
    }

    [Fact]
    public void Legacy_sln_with_one_project_resolves_to_that_project()
    {
        string dir = NewTempDir();
        File.WriteAllText(Path.Combine(dir, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string sln = Path.Combine(dir, "App.sln");
        File.WriteAllText(sln, """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Global
            EndGlobal
            """);

        string? resolved = WatchedSolutionInfo.ResolveSingleProject(sln);
        Assert.NotNull(resolved);
        Assert.EndsWith("App.csproj", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sln_with_a_solution_folder_still_resolves_the_single_real_project()
    {
        string dir = NewTempDir();
        File.WriteAllText(Path.Combine(dir, "Web.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string sln = Path.Combine(dir, "Web.sln");
        // Solution folders carry no .csproj, so they must not count toward the project total.
        File.WriteAllText(sln, """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Solution Items", "Solution Items", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Web", "Web.csproj", "{33333333-3333-3333-3333-333333333333}"
            EndProject
            """);

        Assert.EndsWith("Web.csproj", WatchedSolutionInfo.ResolveSingleProject(sln)!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Multi_project_sln_returns_null_so_it_falls_back()
    {
        string dir = NewTempDir();
        File.WriteAllText(Path.Combine(dir, "A.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(dir, "B.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string sln = Path.Combine(dir, "Two.sln");
        File.WriteAllText(sln, """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "A", "A.csproj", "{44444444-4444-4444-4444-444444444444}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "B", "B.csproj", "{55555555-5555-5555-5555-555555555555}"
            EndProject
            """);

        Assert.Null(WatchedSolutionInfo.ResolveSingleProject(sln));
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "AIMonitorResolveSingle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
