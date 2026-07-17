using System;

namespace ExternalCorpus;

[AttributeUsage(AttributeTargets.Class)]
internal sealed class MarkerAttribute : Attribute
{
}

[/*<bind>*/Marker/*</bind>*/]
internal sealed class AttributeTarget
{
}
