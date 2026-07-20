namespace ExternalCorpus;

internal delegate int DelegateTarget(int value);

internal sealed class DelegateInvocationCases
{
    private static int Target(int value) => value + 1;

    private readonly DelegateTarget handler = Target;

    public int Caller() => /*<bind>*/handler/*</bind>*/(1);
}
