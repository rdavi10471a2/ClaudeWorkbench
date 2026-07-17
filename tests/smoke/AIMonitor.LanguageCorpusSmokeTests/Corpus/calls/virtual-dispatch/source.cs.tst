namespace ExternalCorpus;

internal abstract class VirtualBase
{
    public virtual int Target() => 1;
}

internal sealed class VirtualDerived : VirtualBase
{
    public override int Target() => 2;
}

internal sealed class VirtualCaller
{
    private readonly VirtualBase value = new VirtualDerived();

    public int Caller() => value./*<bind>*/Target/*</bind>*/();
}
