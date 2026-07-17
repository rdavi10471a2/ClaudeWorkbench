namespace ClaudeWorkbench.Host.Console.Models;

public sealed class ReviewActionResult
{
    public ReviewActionResult(string message)
    {
        Message = message;
    }

    public string Message { get; }
}
