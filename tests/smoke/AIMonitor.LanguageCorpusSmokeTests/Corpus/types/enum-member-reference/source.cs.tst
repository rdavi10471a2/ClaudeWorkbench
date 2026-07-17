namespace ExternalCorpus;

internal enum EnumMemberCases
{
    None,
    Ready
}

internal sealed class EnumMemberCaller
{
    public EnumMemberCases Caller() => EnumMemberCases./*<bind>*/Ready/*</bind>*/;
}
