namespace ExternalCorpus;

internal sealed class FieldReadCases
{
    private int count = 4;

    public int Caller() => /*<bind>*/count/*</bind>*/ + 1;
}
