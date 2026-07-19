namespace ClaudeWorkbench.Host.Services;

// Tracks whether a solution index rebuild is in flight, so the UI can show a spinner.
// Set by the background startup rebuild (so reopening the app with a solution attached
// warms the index without a manual Source-tab rebuild) and by the manual Source-tab
// rebuild. A counter, not a bool, so overlapping rebuilds don't clear the flag early.
public sealed class IndexRebuildStatus
{
    private int active;

    public bool IsRebuilding => Volatile.Read(ref active) > 0;

    public event Action? Changed;

    // Mark a rebuild as started; dispose the returned scope when it finishes.
    public IDisposable Begin()
    {
        Interlocked.Increment(ref active);
        Changed?.Invoke();
        return new Scope(this);
    }

    private void End()
    {
        Interlocked.Decrement(ref active);
        Changed?.Invoke();
    }

    private sealed class Scope : IDisposable
    {
        private readonly IndexRebuildStatus owner;
        private bool disposed;

        public Scope(IndexRebuildStatus owner) => this.owner = owner;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            owner.End();
        }
    }
}
