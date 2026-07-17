namespace ExternalCorpus;

internal readonly struct ConversionCases
{
    private readonly int value;

    public ConversionCases(int value)
    {
        this.value = value;
    }

    public static implicit operator int(ConversionCases item) => item.value;

    public int Caller() => /*<bind>*/this/*</bind>*/;
}
