using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// Los operadores de la plataforma (spec 011, ADR-016). Lee y escribe <c>PlatformOperators</c>, y es el sitio que garantiza la
/// regla R1: una cuenta es de empresa o de plataforma, nunca de las dos.
/// </summary>
public sealed class PlatformOperators(ApplicationDbContext dbContext) : IPlatformOperators
{
    public Task<bool> IsActiveOperatorAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.PlatformOperators.AsNoTracking().AnyAsync(o => o.UserId == userId && o.DisabledAt == null, cancellationToken);

    public async Task<PlatformOperatorCreation> AddAsync(Guid userId, DateTime utcNow, CancellationToken cancellationToken)
    {
        // R1: ¿tiene la cuenta una pertenencia a alguna empresa? No hay empresa activa (el operador no tiene ninguna), así que
        // la consulta se salta el filtro de empresa a propósito (ADR-002; está en la lista blanca del test de arquitectura,
        // con este motivo). Mira solo si existe una fila, nunca la lee ni la devuelve.
        var belongsToACompany = await dbContext.TenantUsers
            .IgnoreQueryFilters()
            .AnyAsync(tu => tu.UserId == userId, cancellationToken);

        if (belongsToACompany)
        {
            return PlatformOperatorCreation.AccountBelongsToACompany;
        }

        if (await dbContext.PlatformOperators.AnyAsync(o => o.UserId == userId, cancellationToken))
        {
            return PlatformOperatorCreation.AlreadyAnOperator;
        }

        dbContext.PlatformOperators.Add(PlatformOperator.Create(userId, utcNow));
        return PlatformOperatorCreation.Added;
    }

    public async Task<bool> DisableAsync(Guid userId, DateTime utcNow, CancellationToken cancellationToken)
    {
        var platformOperator = await dbContext.PlatformOperators.FirstOrDefaultAsync(o => o.UserId == userId, cancellationToken);
        if (platformOperator is not { IsActive: true })
        {
            return false;
        }

        platformOperator.Disable(utcNow);
        return true;
    }
}
