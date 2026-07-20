namespace ExternalCorpus;

internal sealed partial class PartialThing
{
    public int B() => 2;
}

internal sealed class PartialCaller
{
    public object Caller() => typeof(/*<bind>*/PartialThing/*</bind>*/);
}
