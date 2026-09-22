using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Customers.Commands.DeleteCustomer;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Customers;

namespace OfiFlow.Application.Tests.Customers;

public class DeleteCustomerCommandHandlerTests
{
    [Fact]
    public async Task Handle_RemovesExistingCustomer()
    {
        await using var db = TestDbContextFactory.Create();
        var customer = Customer.Create(Guid.NewGuid(), CustomerType.Person, "Juan", null, null, null, null);
        db.Customers.Add(customer);
        await db.SaveChangesAsync(CancellationToken.None);

        var handler = new DeleteCustomerCommandHandler(db);
        await handler.Handle(new DeleteCustomerCommand(customer.Id), CancellationToken.None);

        Assert.False(await db.Customers.AnyAsync(c => c.Id == customer.Id));
    }

    [Fact]
    public async Task Handle_WithUnknownId_ThrowsNotFound()
    {
        await using var db = TestDbContextFactory.Create();
        var handler = new DeleteCustomerCommandHandler(db);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.Handle(new DeleteCustomerCommand(Guid.NewGuid()), CancellationToken.None));
    }
}
