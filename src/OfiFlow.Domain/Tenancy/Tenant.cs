using OfiFlow.Domain.Common;

namespace OfiFlow.Domain.Tenancy;

public sealed class Tenant : AggregateRoot, IAuditable
{
    // Longitud máxima: fuente única para EF Core y los Validators (ADR-009 R3).
    public const int NameMaxLength = 200;

    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// Cuándo se suspendió la empresa (hora del servidor, UTC); nulo si está activa. Una empresa suspendida conserva todos
    /// sus datos, pero sus miembros no pueden entrar ni seguir usando una sesión (spec 011, ADR-016).
    /// </summary>
    public DateTime? SuspendedAt { get; private set; }

    public bool IsSuspended => SuspendedAt is not null;

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    private Tenant()
    {
        // Requerido por EF Core.
    }

    private Tenant(Guid id, string name)
        : base(id)
    {
        Name = name;
    }

    public static Tenant Create(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException(TenancyErrors.TenantNameRequired);
        }

        return new Tenant(Guid.NewGuid(), name.Trim());
    }

    /// <summary>
    /// Suspende la empresa. La hora la pone quien llama (el reloj del servidor), no el dominio: así se puede probar con
    /// un reloj controlado. Suspender una ya suspendida es un error de negocio y no un éxito silencioso, para que
    /// quien actúa sepa que no cambió nada (spec 011 R12).
    /// </summary>
    public void Suspend(DateTime utcNow)
    {
        if (IsSuspended)
        {
            throw new DomainException(TenancyErrors.TenantAlreadySuspended);
        }

        SuspendedAt = utcNow;
    }

    /// <summary>Reactiva la empresa. Reactivar una que no está suspendida es un error de negocio (spec 011 R12).</summary>
    public void Reactivate()
    {
        if (!IsSuspended)
        {
            throw new DomainException(TenancyErrors.TenantNotSuspended);
        }

        SuspendedAt = null;
    }
}
