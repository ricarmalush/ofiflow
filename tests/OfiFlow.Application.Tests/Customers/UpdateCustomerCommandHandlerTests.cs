using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Customers.Commands.UpdateCustomer;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Customers;

namespace OfiFlow.Application.Tests.Customers;

public class UpdateCustomerCommandHandlerTests
{
    [Fact]
    public async Task Handle_UpdatesExistingCustomer()
    {
        await using var db = TestDbContextFactory.Create();
        var customer = Customer.Create(Guid.NewGuid(), CustomerType.Person, "Juan", null, null, null, null);
        db.Customers.Add(customer);
        await db.SaveChangesAsync(CancellationToken.None);

        var handler = new UpdateCustomerCommandHandler(db);
        await handler.Handle(
            new UpdateCustomerCommand(customer.Id, "Juan Actualizado", "juan@test.com", null, "Calle Mayor 1", null),
            CancellationToken.None);

        var updated = await db.Customers.FirstAsync(c => c.Id == customer.Id);
        Assert.Equal("Juan Actualizado", updated.Name);
        Assert.Equal("juan@test.com", updated.Email?.Value);
        Assert.Equal("Calle Mayor 1", updated.Address);
    }

    [Fact]
    public async Task Handle_WithUnknownId_ThrowsNotFound()
    {
        await using var db = TestDbContextFactory.Create();
        var handler = new UpdateCustomerCommandHandler(db);

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.Handle(new UpdateCustomerCommand(Guid.NewGuid(), "Juan", null, null, null, null), CancellationToken.None));

        Assert.Equal(CustomerErrors.NotFound, exception.Code);
    }
}
