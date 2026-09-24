using OfiFlow.Domain.Common;

namespace OfiFlow.Domain.Identity;

/// <summary>
/// Business data about a person — deliberately independent of ASP.NET Core Identity
/// (that's ApplicationUser, in Infrastructure/Identity, sharing the same Id — ADR-007).
/// Not ITenantOwned: a User can belong to several Tenants via TenantUser.
/// </summary>
public sealed class User : AggregateRoot, IAuditable
{
    // Longitud máxima: fuente única para EF Core y los Validators (ADR-009 R3).
    public const int NameMaxLength = 200;

    public string Name { get; private set; } = string.Empty;

    public Email ContactEmail { get; private set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    private User()
    {
        // Requerido por EF Core.
    }

    private User(Guid id, string name, Email contactEmail)
        : base(id)
    {
        Name = name;
        ContactEmail = contactEmail;
    }

    /// <param name="id">Debe coincidir con el Id del ApplicationUser creado en la misma transacción (ADR-007).</param>
    public static User Create(Guid id, string name, Email contactEmail)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("El nombre del usuario es obligatorio.", nameof(name));
        }

        return new User(id, name.Trim(), contactEmail);
    }
}
