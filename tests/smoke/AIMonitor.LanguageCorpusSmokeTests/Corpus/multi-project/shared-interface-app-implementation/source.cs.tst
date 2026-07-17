namespace ExternalCorpus.App;

internal sealed class AppInterfaceCaller
{
    public int Caller(ExternalCorpus.Contracts.ISharedService service)
    {
        return service./*<bind>*/Run/*</bind>*/();
    }
}

internal sealed class AppSharedService : ExternalCorpus.Contracts.ISharedService
{
    public int Run() => 42;
}
