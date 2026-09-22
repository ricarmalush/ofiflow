using OfiFlow.Application.Customers.Queries.GetCustomers;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Customers;

namespace OfiFlow.Application.Tests.Customers;

public class GetCustomersQueryHandlerTests
{
    [Fact]
    public async Task Handle_ReturnsAllCustomers_OrderedByName()
    {
        await using var db = TestDbContextFactory.Create();
        db.Customers.Add(Customer.Create(Guid.NewGuid(), CustomerType.Person, "Zoe", null, null, null, null));
        db.Customers.Add(Customer.Create(Guid.NewGuid(), CustomerType.Person, "Ana", null, null, null, null));
        await db.SaveChangesAsync(CancellationToken.None);

        var handler = new GetCustomersQueryHandler(db);
        var result = await handler.Handle(new GetCustomersQuery(), CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("Ana", result[0].Name);
        Assert.Equal("Zoe", result[1].Name);
    }
}
