namespace ExternalCorpus.BlockScoped
{
    internal sealed class BlockScopedTarget
    {
    }

    internal sealed class BlockScopedCaller
    {
        public object Caller() => typeof(/*<bind>*/BlockScopedTarget/*</bind>*/);
    }
}
