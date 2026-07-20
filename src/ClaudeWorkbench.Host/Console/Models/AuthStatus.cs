namespace ClaudeWorkbench.Host.Console;

// Login state of the two CLIs the workbench depends on, surfaced to the command-bar
// dots. Distinct from ConsoleStatus (turn/session state): auth is orthogonal to
// whether a turn is running. Each flag is tri-state — null means "not yet known"
// (the probe has not answered, the sidecar is down, or the CLI is missing), which the
// UI renders as a neutral "checking" dot rather than a false "signed out".
//   Claude  — from the sidecar's `claude auth status` probe
//   GitHub  — from GitService's `gh auth status` probe
public sealed record AuthStatus(bool? Claude, bool? GitHub)
{
    public static AuthStatus Unknown { get; } = new(null, null);
}
