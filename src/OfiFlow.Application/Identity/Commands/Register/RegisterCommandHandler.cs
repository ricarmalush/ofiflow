using MediatR;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Commands.Register;

public sealed class RegisterCommandHandler(IApplicationDbContext dbContext, IIdentityService identityService)
    : IRequestHandler<RegisterCommand, Guid>
{
    public async Task<Guid> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        var userId = Guid.NewGuid();

        var identityResult = await identityService.CreateUserAsync(userId, request.Email, request.Password, cancellationToken);
        if (!identityResult.Succeeded)
        {
            throw new IdentityOperationException(identityResult.Errors);
        }

        var contactEmail = Email.Create(request.Email);
        var tenant = Tenant.Create(request.CompanyName);
        var user = User.Create(userId, request.UserName, contactEmail);
        var tenantUser = TenantUser.CreateOwner(tenant.Id, userId);

        dbContext.Tenants.Add(tenant);
        dbContext.Users.Add(user);
        dbContext.TenantUsers.Add(tenantUser);

        await dbContext.SaveChangesAsync(cancellationToken);

        return tenant.Id;
    }
}
