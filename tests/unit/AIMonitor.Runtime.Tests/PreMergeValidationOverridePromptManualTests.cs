using AIMonitor.Runtime;

namespace AIMonitor.Runtime.Tests;

public sealed class PreMergeValidationOverridePromptManualTests
{
    // Manual-only: PreMergeValidationOverridePrompt.Prompt draws a blocking native dialog and waits
    // for an operator click, so it would hang a normal/CI run. It is gated behind an env var and is a
    // no-op pass unless you opt in. To actually trip and see the dialog (Windows, interactive session):
    //
    //   $env:AIMONITOR_MANUAL_DIALOG_TEST = "1"
    //   dotnet test tests/unit/AIMonitor.Runtime.Tests --filter "FullyQualifiedName~Trips_the_validation_override_dialog"
    //
    // "Yes Launch" returns true, "Cancel" returns false. This only verifies the dialog renders and
    // returns a result; it does not assert which button you press.
    [Fact]
    public void Trips_the_validation_override_dialog_with_a_synthetic_error()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("AIMONITOR_MANUAL_DIALOG_TEST"), "1", StringComparison.Ordinal))
        {
            return;
        }

        IReadOnlyList<string> diagnostics =
        [
            "FAKE001: Synthetic pre-merge validation error used to trip the override dialog.",
            "Watched/Example.cs(42,13): error CS0103: The name 'DoesNotExist' does not exist in the current context."
        ];

        bool launchApproved = PreMergeValidationOverridePrompt.Prompt(diagnostics);

        // The dialog returned a result either way; nothing else to assert for a visual trip test.
        Assert.True(launchApproved == true || launchApproved == false);
    }
}
