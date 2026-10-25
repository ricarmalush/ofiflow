using OfiFlow.Application.Platform;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// Una fila de la auditoría de plataforma (spec 011 R17, D7): qué hizo un operador, sobre qué empresa y cuándo. <b>Solo
/// se inserta</b>: la aplicación no la modifica ni la borra (el <c>ApplicationDbContext</c> lo impide), porque una
/// auditoría que se puede reescribir no prueba nada. Lleva solo Ids, un código de acción y la hora: ningún dato personal.
/// No es de una empresa: aunque guarde el Id de la empresa afectada, quien la lee es el operador.
/// </summary>
public sealed class PlatformAuditEntry
{
    public Guid Id { get; private set; }

    public Guid OperatorUserId { get; private set; }

    /// <summary>Un código de <see cref="PlatformAuditActions"/>.</summary>
    public string Action { get; private set; } = string.Empty;

    /// <summary>La empresa afectada, si la acción es sobre una (suspender, reactivar); nulo en las demás.</summary>
    public Guid? TenantId { get; private set; }

    public DateTime OccurredAt { get; private set; }

    private PlatformAuditEntry()
    {
        // Requerido por EF Core.
    }

    public static PlatformAuditEntry Create(Guid operatorUserId, string action, Guid? tenantId, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(action.Length, PlatformAuditActions.MaxLength);

        return new PlatformAuditEntry
        {
            Id = Guid.NewGuid(),
            OperatorUserId = operatorUserId,
            Action = action,
            TenantId = tenantId,
            OccurredAt = utcNow
        };
    }
}
