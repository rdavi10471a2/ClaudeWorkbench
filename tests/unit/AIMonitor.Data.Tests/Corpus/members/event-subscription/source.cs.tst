using System;

namespace ExternalCorpus;

internal sealed class EventCases
{
    public event EventHandler? Changed;

    public void Caller()
    {
        /*<bind>*/Changed/*</bind>*/ += (_, _) => { };
    }
}
