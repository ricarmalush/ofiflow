using OfiFlow.Application.Customers.Queries.GetCustomer;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Customers;

namespace OfiFlow.Application.Tests.Customers;

public class GetCustomerQueryHandlerTests
{
    [Fact]
    public async Task Handle_ReturnsCustomerDto_WhenItExists()
    {
        await using var db = TestDbContextFactory.Create();
        var customer = Customer.Create(Guid.NewGuid(), CustomerType.Company, "Ferretería Pérez", null, null, null, null);
        db.Customers.Add(customer);
        await db.SaveChangesAsync(CancellationToken.None);

        var handler = new GetCustomerQueryHandler(db);
        var result = await handler.Handle(new GetCustomerQuery(customer.Id), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Ferretería Pérez", result!.Name);
    }

    [Fact]
    public async Task Handle_ReturnsNull_WhenCustomerDoesNotExist()
    {
        await using var db = TestDbContextFactory.Create();
        var handler = new GetCustomerQueryHandler(db);

        var result = await handler.Handle(new GetCustomerQuery(Guid.NewGuid()), CancellationToken.None);

        Assert.Null(result);
    }
}
