using BlazorSample.Model;

namespace BlazorSample.Repositories;

public sealed class CustomerRepository : RepositoryBase<Customer>, ICustomerRepository
{
    public override Task<Customer?> GetByIdAsync(int id)
    {
        Log("get-customer");
        return base.GetByIdAsync(id);
    }
}
