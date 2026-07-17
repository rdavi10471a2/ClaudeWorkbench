using System;

namespace ExternalCorpus;

internal sealed class LambdaCallerCases
{
    private static int Target(int value) => value + 1;

    public Func<int, int> Caller() => value => /*<bind>*/Target/*</bind>*/(value);
}
