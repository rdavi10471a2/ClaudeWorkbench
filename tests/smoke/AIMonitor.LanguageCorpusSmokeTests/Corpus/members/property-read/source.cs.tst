namespace ExternalCorpus;

internal sealed class PropertyCases
{
    public int Count { get; init; }

    public int Caller() => /*<bind>*/Count/*</bind>*/ + 1;
}
