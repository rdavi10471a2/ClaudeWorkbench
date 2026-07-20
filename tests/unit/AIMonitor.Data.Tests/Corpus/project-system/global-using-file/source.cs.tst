namespace ExternalCorpus.App;

internal sealed class GlobalUsingCaller
{
    public object Caller() => typeof(/*<bind>*/GlobalUsingTarget/*</bind>*/);
}
