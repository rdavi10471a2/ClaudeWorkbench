using WinFormsSample.Model;

namespace WinFormsSample.Repositories;

public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(int id);
}
