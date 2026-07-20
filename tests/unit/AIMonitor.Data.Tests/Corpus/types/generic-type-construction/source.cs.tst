namespace ExternalCorpus;

internal sealed class Box<T>
{
}

internal sealed class GenericTypeCaller
{
    public /*<bind>*/Box/*</bind>*/<int> Caller() => new();
}
