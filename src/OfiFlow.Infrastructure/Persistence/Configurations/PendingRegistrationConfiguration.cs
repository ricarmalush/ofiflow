using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Infrastructure.Persistence.Configurations;

public sealed class PendingRegistrationConfiguration : IEntityTypeConfiguration<PendingRegistration>
{
    /// <summary>Nombre de la columna de concurrencia. Es una propiedad «sombra»: Domain no sabe que existe.</summary>
    public const string RowVersion = "RowVersion";

    public void Configure(EntityTypeBuilder<PendingRegistration> builder)
    {
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Email).HasMaxLength(Domain.Common.Email.MaxLength).IsRequired();
        builder.Property(r => r.NormalizedEmail).HasMaxLength(Domain.Common.Email.MaxLength).IsRequired();
        builder.Property(r => r.CompanyName).HasMaxLength(Tenant.NameMaxLength).IsRequired();
        builder.Property(r => r.UserName).HasMaxLength(User.NameMaxLength).IsRequired();
        builder.Property(r => r.TermsVersion).HasMaxLength(User.TermsVersionMaxLength).IsRequired();
        builder.Property(r => r.TokenHash).HasMaxLength(PendingRegistration.TokenHashMaxLength).IsRequired();

        // Único: el token se busca por su hash (el valor en claro no se guarda) y dos filas con el mismo hash harían ambigua la búsqueda al completar el registro (spec 021 R4).
        builder.HasIndex(r => r.TokenHash).IsUnique();

        // UN SOLO PENDIENTE VIGENTE POR EMAIL (R5), garantizado por la base de datos y no solo por el código: único sobre el email normalizado SOLO mientras la fila no se haya canjeado
        // ni anulado (UsedAt nulo). Dos peticiones simultáneas para el mismo email no pueden dejar dos vigentes: la segunda choca con este índice. Filtrado, para que los canjeados y
        // los anulados (que se conservan hasta la purga) no impidan pedir otro. Ojo: una fila CADUCADA sin canjear sigue teniendo UsedAt nulo, así que quien pide uno nuevo debe
        // anularla también (lo hace PendingRegistrationService), no solo las vigentes.
        builder.HasIndex(r => r.NormalizedEmail)
            .IsUnique()
            .HasFilter("[UsedAt] IS NULL")
            .HasDatabaseName("IX_PendingRegistrations_NormalizedEmail_Active");

        // «¿Cuántos se crearon para este email en la última hora?» (R7): por email y por fecha.
        builder.HasIndex(r => new { r.NormalizedEmail, r.CreatedAt });

        // La purga (R18) borra por rango de caducidad: sin este índice cada ciclo leería la tabla entera.
        builder.HasIndex(r => r.ExpiresAt);

        // Control de concurrencia optimista (R13): dos canjes simultáneos con el mismo token leen la misma versión; el segundo en guardar encuentra que ya cambió y falla, en vez de crear
        // la empresa dos veces. Un «if» en el código no lo garantiza: sería comprobar y escribir en dos pasos.
        builder.Property<byte[]>(RowVersion).IsRowVersion();

        // Deliberadamente SIN clave foránea (todavía no hay cuenta ni empresa a las que apuntar) y SIN ningún Id de tenant.
    }
}
