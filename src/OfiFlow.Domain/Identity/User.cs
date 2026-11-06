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

    /// <summary>Longitud máxima de la versión de las condiciones de uso (spec 021): una etiqueta corta como <c>2026-11</c>, no un texto. Fuente única para EF Core y los validadores.</summary>
    public const int TermsVersionMaxLength = 20;

    public string Name { get; private set; } = string.Empty;

    public Email ContactEmail { get; private set; } = null!;

    /// <summary>
    /// La versión de las condiciones de uso que aceptó al crear su cuenta (spec 021 R16), o nulo si la cuenta es anterior a que existieran: esas cuentas no cambian (R19) y
    /// aceptarlas es una spec posterior. Los textos no se guardan aquí ni los decide el código: solo la etiqueta de la versión.
    /// </summary>
    public string? TermsVersion { get; private set; }

    /// <summary>Cuándo las aceptó (UTC), junto con <see cref="TermsVersion"/>.</summary>
    public DateTime? TermsAcceptedAt { get; private set; }

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
            throw new DomainException(IdentityErrors.UserNameRequired);
        }

        return new User(id, name.Trim(), contactEmail);
    }

    /// <summary>
    /// Deja constancia de las condiciones de uso que aceptó (spec 021 R16). La versión ya la comprobó Application contra la vigente; aquí una etiqueta vacía o que no cabe es un
    /// fallo de programación y lanza, no un error de negocio.
    /// </summary>
    public void AcceptTerms(string version, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(version.Length, TermsVersionMaxLength);

        TermsVersion = version;
        TermsAcceptedAt = utcNow;
    }
}
