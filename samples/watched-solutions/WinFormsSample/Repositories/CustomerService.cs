using WinFormsSample.Model;

namespace WinFormsSample.Repositories;

public sealed class CustomerService
{
    private readonly CustomerRepository repository = new CustomerRepository();

    public Task<Customer?> LoadAsync(int id)
    {
        return repository.GetByIdAsync(id);
    }

    public Customer CreateDefault()
    {
        Customer customer = new Customer();
        return customer;
    }
}
