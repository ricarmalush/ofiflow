using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Customers.Commands.CreateCustomer;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Customers;

namespace OfiFlow.Application.Tests.Customers;

public class CreateCustomerCommandHandlerTests
{
    [Fact]
    public async Task Handle_CreatesCustomer_WithActiveTenantId()
    {
        await using var db = TestDbContextFactory.Create();
        var tenantContext = new FakeTenantContext(Guid.NewGuid());
        var handler = new CreateCustomerCommandHandler(db, tenantContext);

        var id = await handler.Handle(
            new CreateCustomerCommand(CustomerType.Person, "Juan Pérez", "juan@example.com", null, null, null),
            CancellationToken.None);

        var customer = await db.Customers.FirstAsync(c => c.Id == id);
        Assert.Equal(tenantContext.TenantId, customer.TenantId);
        Assert.Equal("Juan Pérez", customer.Name);
        Assert.Equal("juan@example.com", customer.Email?.Value);
    }
}
