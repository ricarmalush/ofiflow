using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// Escribe la auditoría de plataforma (spec 011 R17). Solo <b>añade</b> al contexto, sin confirmar: la fila entra en el
/// mismo SaveChanges que la acción que audita. Nunca lee la tabla ni modifica una fila.
/// </summary>
public sealed class PlatformAudit(ApplicationDbContext dbContext) : IPlatformAudit
{
    public void Add(Guid operatorUserId, string action, Guid? tenantId, DateTime utcNow) =>
        dbContext.PlatformAuditEntries.Add(PlatformAuditEntry.Create(operatorUserId, action, tenantId, utcNow));
}
