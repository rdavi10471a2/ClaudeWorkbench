namespace ExternalCorpus;

internal sealed class PropertyWriteCases
{
    public int Count { get; private set; }

    public void Caller()
    {
        /*<bind>*/Count/*</bind>*/ = 2;
    }
}
