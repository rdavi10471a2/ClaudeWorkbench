namespace ExternalCorpus;

internal interface IExplicitContract
{
    int Target();
}

internal sealed class ExplicitContractImpl : IExplicitContract
{
    int IExplicitContract.Target() => 7;
}

internal sealed class ExplicitInterfaceCaller
{
    private readonly IExplicitContract contract = new ExplicitContractImpl();

    public int Caller() => contract./*<bind>*/Target/*</bind>*/();
}
