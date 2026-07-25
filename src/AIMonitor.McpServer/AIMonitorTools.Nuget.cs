using AIMonitor.Workflow;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace AIMonitor.McpServer;

public sealed partial class AIMonitorTools
{
    [McpServerTool]
    [Description("Run `dotnet restore` on the watched solution (host-executed from the solution root — you never run a shell yourself). Use it after you add a NuGet `<PackageReference>` to a project's .csproj (a governed edit) so the package's assets are restored for the pre-merge build AND the index; it is also safe to call when a package-bearing project seems unrestored. Restore is a no-op when packages are already current. Returns the outcome and any NuGet/build diagnostics.")]
    public SolutionRestoreService.RestoreResult RestoreSolution()
    {
        runtimeState.Touch();
        return new SolutionRestoreService().Restore(settings);
    }
}
