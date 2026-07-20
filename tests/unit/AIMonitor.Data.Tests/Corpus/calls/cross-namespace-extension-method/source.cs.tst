using ExternalCorpus.Extensions;

namespace ExternalCorpus;

internal sealed class CrossNamespaceExtensionCaller
{
    public int Caller() => 5./*<bind>*/Triple/*</bind>*/();
}
