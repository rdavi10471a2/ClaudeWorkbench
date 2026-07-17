namespace ExternalCorpus;

internal sealed class RequiredMemberCases
{
    public required string Name { get; init; }
}

internal sealed class RequiredMemberCaller
{
    public RequiredMemberCases Caller() => new RequiredMemberCases { /*<bind>*/Name/*</bind>*/ = "Ada" };
}
