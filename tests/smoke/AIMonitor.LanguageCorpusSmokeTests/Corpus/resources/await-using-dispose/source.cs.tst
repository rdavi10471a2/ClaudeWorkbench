using System.Threading.Tasks;

namespace ExternalCorpus;

internal sealed class AsyncResource
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class AwaitUsingCases
{
    public async Task CallerAsync()
    {
        /*<bind>*/await/*</bind>*/ using AsyncResource resource = new();
    }
}
