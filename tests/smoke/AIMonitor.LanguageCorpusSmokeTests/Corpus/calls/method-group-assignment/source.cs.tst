namespace ExternalCorpus;

internal delegate int MethodGroupDelegate(int value);

internal sealed class MethodGroupCases
{
    private static int Target(int value) => value + 1;

    public MethodGroupDelegate Caller() => /*<bind>*/Target/*</bind>*/;
}
