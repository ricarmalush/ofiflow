using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Persistence;

namespace OfiFlow.Application.Tenancy.Queries.GetUsers;

public sealed class GetUsersQueryHandler(IApplicationDbContext dbContext, ICurrentUser currentUser)
    : IRequestHandler<GetUsersQuery, List<TenantUserDto>>
{
    public async Task<List<TenantUserDto>> Handle(GetUsersQuery request, CancellationToken cancellationToken)
    {
        // El filtro de empresa actúa sobre TenantUsers: solo salen las pertenencias de la empresa activa. La ficha de
        // cada persona (User) no tiene filtro propio (puede estar en varias empresas, ADR-004), pero aquí solo se llega a
        // ella desde esas pertenencias, nunca se consulta suelta. Por eso este fichero está en la lista blanca de lectura
        // de Users, y un test contra SQL Server real comprueba que una empresa no ve a los miembros de otra.
        var members = await (
                from tenantUser in dbContext.TenantUsers
                join user in dbContext.Users on tenantUser.UserId equals user.Id
                orderby user.Name
                select new { tenantUser.Id, tenantUser.UserId, user.Name, user.ContactEmail, tenantUser.Role, tenantUser.CreatedAt })
            .ToListAsync(cancellationToken);

        return members
            .Select(m => new TenantUserDto(m.Id, m.Name, m.ContactEmail.Value, m.Role.ToString(), m.CreatedAt, m.UserId == currentUser.UserId))
            .ToList();
    }
}
