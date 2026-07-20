namespace ExternalCorpus;

internal sealed class PatternTarget
{
}

internal sealed class PatternTypeCases
{
    public bool Caller(object value) => value is /*<bind>*/PatternTarget/*</bind>*/;
}
