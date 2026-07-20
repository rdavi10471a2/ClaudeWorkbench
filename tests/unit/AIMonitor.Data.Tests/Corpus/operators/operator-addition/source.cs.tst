namespace ExternalCorpus;

internal readonly struct OperatorCases
{
    private readonly int value;

    public OperatorCases(int value)
    {
        this.value = value;
    }

    public static OperatorCases operator +(OperatorCases left, OperatorCases right)
    {
        return new OperatorCases(left.value + right.value);
    }

    public OperatorCases Caller(OperatorCases other) => this /*<bind>*/+/*</bind>*/ other;
}
