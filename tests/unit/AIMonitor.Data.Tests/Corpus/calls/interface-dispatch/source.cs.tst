namespace ExternalCorpus;

internal interface IInterfaceDispatchService
{
    int Target(int value);
}

internal sealed class InterfaceDispatchService : IInterfaceDispatchService
{
    public int Target(int value) => value + 1;
}

internal sealed class InterfaceDispatchCaller
{
    private readonly IInterfaceDispatchService service = new InterfaceDispatchService();

    public int Caller() => service./*<bind>*/Target/*</bind>*/(1);
}
