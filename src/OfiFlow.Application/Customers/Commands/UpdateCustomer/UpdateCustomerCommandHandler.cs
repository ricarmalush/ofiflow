using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Customers;

namespace OfiFlow.Application.Customers.Commands.UpdateCustomer;

public sealed class UpdateCustomerCommandHandler(IApplicationDbContext dbContext) : IRequestHandler<UpdateCustomerCommand>
{
    public async Task Handle(UpdateCustomerCommand request, CancellationToken cancellationToken)
    {
        var customer = await dbContext.Customers.FirstOrDefaultAsync(c => c.Id == request.Id, cancellationToken)
            ?? throw new NotFoundException(CustomerErrors.NotFound, request.Id);

        var email = string.IsNullOrWhiteSpace(request.Email) ? null : Email.Create(request.Email);
        var phone = string.IsNullOrWhiteSpace(request.Phone) ? null : PhoneNumber.Create(request.Phone);

        customer.UpdateContactInfo(request.Name, email, phone, request.Address, request.Notes);

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
