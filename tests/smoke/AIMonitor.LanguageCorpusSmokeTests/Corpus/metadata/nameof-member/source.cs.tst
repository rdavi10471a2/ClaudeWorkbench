namespace ExternalCorpus;

internal sealed class NameOfCases
{
    public void Target()
    {
    }

    public string Caller() => nameof(/*<bind>*/Target/*</bind>*/);
}
