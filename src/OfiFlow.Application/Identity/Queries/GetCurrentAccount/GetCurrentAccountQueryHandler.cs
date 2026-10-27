using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Queries.GetCurrentAccount;

public sealed class GetCurrentAccountQueryHandler(IApplicationDbContext dbContext, ICurrentUser currentUser)
    : IRequestHandler<GetCurrentAccountQuery, CurrentAccountDto>
{
    public async Task<CurrentAccountDto> Handle(GetCurrentAccountQuery request, CancellationToken cancellationToken)
    {
        // Quién pregunta sale del token validado y el rol, de la base de datos (lo verificó el AuthorizationBehavior antes de llegar
        // aquí). Sin cualquiera de los tres no hay nada que decir: ante la duda, se deniega.
        var userId = currentUser.UserId ?? throw new ForbiddenException(TenancyErrors.Forbidden);
        var tenantId = currentUser.TenantId ?? throw new ForbiddenException(TenancyErrors.Forbidden);
        var role = currentUser.Role ?? throw new ForbiddenException(TenancyErrors.Forbidden);

        // Se parte de la PERTENENCIA (TenantUsers lleva el filtro de empresa) y solo desde esa fila se llega a la ficha de la persona
        // y a su empresa: ni Users ni Tenants se consultan sueltos. Por eso este fichero está en las listas blancas de lectura de esas
        // dos tablas, y un test contra SQL Server real comprueba que una empresa no ve nada de otra. El Id de empresa se repite a
        // propósito en la consulta (además del filtro global): defensa en profundidad y un test de este handler que no depende del filtro.
        var account = await (
                from membership in dbContext.TenantUsers
                join user in dbContext.Users on membership.UserId equals user.Id
                join tenant in dbContext.Tenants on membership.TenantId equals tenant.Id
                where membership.UserId == userId && membership.TenantId == tenantId
                select new { MembershipId = membership.Id, user.Name, user.ContactEmail, TenantId = tenant.Id, TenantName = tenant.Name })
            .SingleOrDefaultAsync(cancellationToken);

        // La pertenencia ya se comprobó; si aun así no está (se borró entre medias), no hay respuesta que dar.
        if (account is null)
        {
            throw new ForbiddenException(TenancyErrors.Forbidden);
        }

        return new CurrentAccountDto(
            userId,
            account.MembershipId,
            account.Name,
            account.ContactEmail.Value,
            account.TenantId,
            account.TenantName,
            role.ToString(),
            PermissionNames.For(role));
    }
}
