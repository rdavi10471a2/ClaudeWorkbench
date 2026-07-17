using BlazorSample.Model;

namespace BlazorSample.Repositories;

public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(int id);
}
