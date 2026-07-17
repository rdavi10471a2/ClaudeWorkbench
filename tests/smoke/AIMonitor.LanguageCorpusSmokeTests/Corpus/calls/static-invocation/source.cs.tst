namespace ExternalCorpus;

internal sealed class InvocationCases
{
    private static int Target(int value) => value + 1;

    public int Caller() => /*<bind>*/Target/*</bind>*/(1);
}
