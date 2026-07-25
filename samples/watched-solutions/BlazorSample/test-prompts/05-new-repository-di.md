# 05 — New repository behind the interface (multi-file, DI-shaped)

**Tests:** adding a new implementation of an existing interface and rewiring the consumer —
the composition-root shape — across a new file plus an edit, in one session. The plan-complete
build confirms the new type satisfies `ICustomerRepository` and the consumer still compiles.

## Prompt

Add a new file `Repositories/InMemoryCustomerRepository.cs` with a
`public sealed class InMemoryCustomerRepository : ICustomerRepository` that stores a small
seeded list of `Customer` and implements `GetByIdAsync(int id)` by returning the matching
customer (or null). Then change `CustomerService` to use `InMemoryCustomerRepository` instead
of `CustomerRepository`. Everything must still compile as one session.
