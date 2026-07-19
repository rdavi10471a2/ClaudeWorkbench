using System.Net.Sockets;
using System.Text;

namespace ClaudeWorkbench.Launcher;

// Headless verification of the lifecycle mechanism (no GUI, no browser window): start a
// workspace instance, confirm the host AND the sidecar it spawns are both up, then Stop
// (terminate the Job Object) and confirm BOTH are gone. Proves "kill one, kill both".
// Writes a report to the given log path and returns 0 on success. Invoked with:
//   ClaudeWorkbench.Launcher.exe --selftest <solution> <logPath>
internal static class SelfTest
{
    public static async Task<int> Run(string solutionPath, string? logPath)
    {
        StringBuilder log = new();
        void Line(string message) => log.AppendLine(message);

        int code;
        try
        {
            LauncherState state = LauncherState.Load();
            state.Browser = BrowserKind.DefaultShell; // irrelevant; browser is skipped below
            Line($"launcherExe={AppContext.BaseDirectory}");
            Line($"workbenchRoot={state.WorkbenchRoot ?? "(not found)"}");
            Line($"statePath={LauncherState.StatePath}");
            Line($"hostExe={state.HostExePath}");
            Line($"sidecarDir={state.SidecarDirectory}");
            Line($"instancesRoot={state.EffectiveInstancesRoot}");

            WorkspaceEntry workspace = new() { Name = "selftest", SolutionPath = solutionPath };
            using InstanceController controller = new(workspace, state);
            Line($"instanceDir={state.InstanceDirectoryFor(workspace)}");

            await controller.StartAsync(Array.Empty<int>(), launchBrowser: false);
            Line($"status={controller.Status} host={controller.HostPort} sidecar={controller.SidecarPort} err={controller.LastError}");

            if (controller.Status != InstanceStatus.Running)
            {
                Line("RESULT=FAIL (did not reach Running)");
                code = 3;
            }
            else
            {
                // The host reports healthy before its sidecar child has finished binding, so
                // both ports have to be waited on rather than probed once. A fresh install
                // (cold node_modules) is comfortably slower than a warm dev checkout.
                bool hostUp = await WaitForPort(controller.HostPort, open: true, TimeSpan.FromSeconds(30));
                bool sidecarUp = await WaitForPort(controller.SidecarPort, open: true, TimeSpan.FromSeconds(30));
                Line($"after start: hostUp={hostUp} sidecarUp={sidecarUp}");

                controller.Stop();

                bool hostClosed = await WaitForPort(controller.HostPort, open: false, TimeSpan.FromSeconds(15));
                bool sidecarClosed = await WaitForPort(controller.SidecarPort, open: false, TimeSpan.FromSeconds(15));
                Line($"after stop:  hostClosed={hostClosed} sidecarClosed={sidecarClosed}");

                bool ok = hostUp && sidecarUp && hostClosed && sidecarClosed;
                Line(ok ? "RESULT=PASS" : "RESULT=FAIL");
                code = ok ? 0 : 4;
            }
        }
        catch (Exception exception)
        {
            Line($"RESULT=FAIL (exception) {exception}");
            code = 5;
        }

        if (!string.IsNullOrWhiteSpace(logPath))
        {
            try
            {
                File.WriteAllText(logPath, log.ToString());
            }
            catch
            {
                // ignore
            }
        }

        return code;
    }

    // Poll until the port reaches the wanted state, or give up. Returns whether it got there.
    private static async Task<bool> WaitForPort(int port, bool open, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Probe(port) == open)
            {
                return true;
            }

            await Task.Delay(250);
        }

        return false;
    }

    private static bool Probe(int port)
    {
        try
        {
            using TcpClient client = new();
            return client.ConnectAsync("127.0.0.1", port).Wait(500) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
