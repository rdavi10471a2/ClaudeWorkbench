#nullable enable

namespace ExternalCorpus;

internal sealed class NullableCases
{
    public string? Name { get; init; }

    public int Caller() => /*<bind>*/Name/*</bind>*/?.Length ?? 0;
}
