namespace ExternalCorpus;

internal sealed record Person(string Name, int Age);

internal sealed class RecordPrimaryConstructorCaller
{
    public Person Caller() => new /*<bind>*/Person/*</bind>*/("Ada", 37);
}
