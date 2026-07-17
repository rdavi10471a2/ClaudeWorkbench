using BlazorSample.Model;

namespace BlazorSample.Repositories;

public sealed class CustomerService
{
    private readonly CustomerRepository repository = new CustomerRepository();

    public Task<Customer?> LoadAsync(int id)
    {
        return repository.GetByIdAsync(id);
    }
}
