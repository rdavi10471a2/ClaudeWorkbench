namespace ExternalCorpus;

internal static class ExtensionCases
{
    public static int Twice(this int value) => value * 2;
}

internal sealed class ExtensionCaller
{
    public int Caller() => 21./*<bind>*/Twice/*</bind>*/();
}
