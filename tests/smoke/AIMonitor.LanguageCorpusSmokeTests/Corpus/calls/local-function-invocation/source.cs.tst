namespace ExternalCorpus;

internal sealed class LocalFunctionCases
{
    public int Caller()
    {
        int Target(int value) => value + 1;
        return /*<bind>*/Target/*</bind>*/(1);
    }
}
