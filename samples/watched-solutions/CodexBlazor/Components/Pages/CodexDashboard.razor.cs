using Microsoft.AspNetCore.Components;

namespace CodexBlazor.Components.Pages;

public class CodexDashboardBase : ComponentBase
{
    protected int ClickCount { get; private set; }

    protected string StatusLabel { get; private set; } = "Ready";

    protected void Increment()
    {
        ClickCount++;
        StatusLabel = ClickCount == 1
            ? "Clicked once"
            : "Clicked more than once";
    }
}
