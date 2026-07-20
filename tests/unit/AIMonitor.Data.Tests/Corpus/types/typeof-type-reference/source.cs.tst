using System;

namespace ExternalCorpus;

internal sealed class TypeOfTarget
{
}

internal sealed class TypeOfCaller
{
    public Type Caller() => typeof(/*<bind>*/TypeOfTarget/*</bind>*/);
}
