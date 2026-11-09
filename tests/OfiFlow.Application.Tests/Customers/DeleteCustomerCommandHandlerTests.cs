using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Customers.Commands.DeleteCustomer;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Jobs;
using OfiFlow.Domain.Quotes;

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

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.Handle(new DeleteCustomerCommand(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(CustomerErrors.NotFound, exception.Code);
    }

    [Fact]
    public async Task Handle_WithActiveJob_ThrowsBusinessRuleExceptionAndKeepsCustomer()
    {
        await using var db = TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var customer = Customer.Create(tenantId, CustomerType.Person, "Juan", null, null, null, null);
        var job = Job.Create(tenantId, customer.Id, "Reparar fuga", null, JobPriority.Normal);
        db.Customers.Add(customer);
        db.Jobs.Add(job);
        await db.SaveChangesAsync(CancellationToken.None);

        var handler = new DeleteCustomerCommandHandler(db);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            handler.Handle(new DeleteCustomerCommand(customer.Id), CancellationToken.None));

        Assert.Equal(CustomerErrors.HasActiveJobs, exception.Code);

        Assert.True(await db.Customers.AnyAsync(c => c.Id == customer.Id));
    }

    [Fact]
    public async Task Handle_WithOnlyCompletedJobs_DeletesCustomer()
    {
        await using var db = TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var customer = Customer.Create(tenantId, CustomerType.Person, "Juan", null, null, null, null);
        var job = Job.Create(tenantId, customer.Id, "Reparar fuga", null, JobPriority.Normal);
        job.Start();
        job.Complete();
        db.Customers.Add(customer);
        db.Jobs.Add(job);
        await db.SaveChangesAsync(CancellationToken.None);

        var handler = new DeleteCustomerCommandHandler(db);
        await handler.Handle(new DeleteCustomerCommand(customer.Id), CancellationToken.None);

        Assert.False(await db.Customers.AnyAsync(c => c.Id == customer.Id));
    }

    // Spec 023 R13: un presupuesto es un documento comercial y no se borra a espaldas de nadie, en cualquier estado.
    [Theory]
    [InlineData(QuoteStatus.Draft)]
    [InlineData(QuoteStatus.Sent)]
    [InlineData(QuoteStatus.Accepted)]
    [InlineData(QuoteStatus.Rejected)]
    public async Task Handle_WithAQuoteInAnyState_IsRefused_AndKeepsTheCustomer(QuoteStatus status)
    {
        await using var db = TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var customer = Customer.Create(tenantId, CustomerType.Person, "Juan", null, null, null, null);
        var quote = Quote.Create(tenantId, customer.Id, null, 1, "Reforma", null, null, [new QuoteItemInput("Mano de obra", 1, 10000, 21)]);
        if (status is not QuoteStatus.Draft)
        {
            quote.Send(DateTime.UtcNow);
        }

        if (status == QuoteStatus.Accepted)
        {
            quote.Accept(DateTime.UtcNow);
        }

        if (status == QuoteStatus.Rejected)
        {
            quote.Reject(DateTime.UtcNow);
        }

        db.Customers.Add(customer);
        db.Quotes.Add(quote);
        await db.SaveChangesAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            new DeleteCustomerCommandHandler(db).Handle(new DeleteCustomerCommand(customer.Id), CancellationToken.None));

        Assert.Equal(CustomerErrors.HasQuotes, exception.Code);
        Assert.True(await db.Customers.AnyAsync(c => c.Id == customer.Id));
        Assert.True(await db.Quotes.AnyAsync(q => q.Id == quote.Id));
    }

    [Fact]
    public async Task Handle_WithQuotesOfAnotherCustomer_StillDeletes()
    {
        await using var db = TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var customer = Customer.Create(tenantId, CustomerType.Person, "Juan", null, null, null, null);
        db.Customers.Add(customer);
        db.Quotes.Add(Quote.Create(tenantId, Guid.NewGuid(), null, 1, "De otro", null, null, []));
        await db.SaveChangesAsync(CancellationToken.None);

        await new DeleteCustomerCommandHandler(db).Handle(new DeleteCustomerCommand(customer.Id), CancellationToken.None);

        Assert.False(await db.Customers.AnyAsync(c => c.Id == customer.Id));
    }
}
