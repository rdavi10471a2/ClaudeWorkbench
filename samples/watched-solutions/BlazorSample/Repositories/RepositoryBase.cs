namespace BlazorSample.Repositories;

public abstract class RepositoryBase<T> where T : class
{
    protected void Log(string message) { }
    public virtual Task<T?> GetByIdAsync(int id) => Task.FromResult<T?>(null);
}
