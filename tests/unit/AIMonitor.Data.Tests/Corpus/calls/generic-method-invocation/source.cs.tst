namespace ExternalCorpus;

internal sealed class GenericMethodCases
{
    private T Echo<T>(T value) => value;

    public int Caller() => /*<bind>*/Echo/*</bind>*/<int>(42);
}
