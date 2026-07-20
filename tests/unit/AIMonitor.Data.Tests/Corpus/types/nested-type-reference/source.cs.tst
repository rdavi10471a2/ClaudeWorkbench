namespace ExternalCorpus;

internal sealed class OuterType
{
    public sealed class Nested
    {
    }
}

internal sealed class NestedTypeCaller
{
    public object Caller() => typeof(OuterType./*<bind>*/Nested/*</bind>*/);
}
