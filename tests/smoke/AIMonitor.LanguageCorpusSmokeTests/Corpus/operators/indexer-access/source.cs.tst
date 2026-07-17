namespace ExternalCorpus;

internal sealed class IndexerCases
{
    public int this[int index] => index;

    public int Caller() => /*<bind>*/this/*</bind>*/[0];
}
