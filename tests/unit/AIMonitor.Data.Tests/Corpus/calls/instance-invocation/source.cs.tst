namespace ExternalCorpus;

internal sealed class InstanceInvocationCases
{
    private int Target(int value) => value + 1;

    public int Caller() => /*<bind>*/Target/*</bind>*/(1);
}
