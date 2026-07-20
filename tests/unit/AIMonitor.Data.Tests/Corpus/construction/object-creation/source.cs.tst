namespace ExternalCorpus;

internal sealed class Created
{
    public Created(int value)
    {
        Value = value;
    }

    public int Value { get; }
}

internal sealed class ConstructorCaller
{
    public Created Caller() => new /*<bind>*/Created/*</bind>*/(42);
}
