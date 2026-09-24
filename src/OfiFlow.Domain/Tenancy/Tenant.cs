using OfiFlow.Domain.Common;

namespace OfiFlow.Domain.Tenancy;

public sealed class Tenant : AggregateRoot, IAuditable
{
    // Longitud máxima: fuente única para EF Core y los Validators (ADR-009 R3).
    public const int NameMaxLength = 200;

    public string Name { get; private set; } = string.Empty;

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
            throw new ArgumentException("El nombre de la empresa es obligatorio.", nameof(name));
        }

        return new Tenant(Guid.NewGuid(), name.Trim());
    }
}
