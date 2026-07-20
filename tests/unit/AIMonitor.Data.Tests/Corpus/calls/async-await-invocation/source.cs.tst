using System.Threading.Tasks;

namespace ExternalCorpus;

internal sealed class AsyncAwaitCases
{
    private static Task<int> FetchAsync() => Task.FromResult(42);

    public async Task<int> CallerAsync() => await /*<bind>*/FetchAsync/*</bind>*/();
}
