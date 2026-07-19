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
            Line($"hostExe={state.HostExePath}");
            Line($"sidecarDir={state.SidecarDirectory}");

            WorkspaceEntry workspace = new() { Name = "selftest", SolutionPath = solutionPath };
            using InstanceController controller = new(workspace, state);

            await controller.StartAsync(Array.Empty<int>(), launchBrowser: false);
            Line($"status={controller.Status} host={controller.HostPort} sidecar={controller.SidecarPort} err={controller.LastError}");

            if (controller.Status != InstanceStatus.Running)
            {
                Line("RESULT=FAIL (did not reach Running)");
                code = 3;
            }
            else
            {
                bool hostUp = Probe(controller.HostPort);
                bool sidecarUp = Probe(controller.SidecarPort);
                Line($"after start: hostUp={hostUp} sidecarUp={sidecarUp}");

                controller.Stop();
                await Task.Delay(2000);

                bool hostClosed = !Probe(controller.HostPort);
                bool sidecarClosed = !Probe(controller.SidecarPort);
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
