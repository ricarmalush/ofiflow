using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Infrastructure.Persistence.Configurations;

public sealed class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    /// <summary>Nombre de la columna de concurrencia. Es una propiedad «sombra»: Domain no sabe que existe.</summary>
    public const string RowVersion = "RowVersion";

    public void Configure(EntityTypeBuilder<PasswordResetToken> builder)
    {
        builder.HasKey(t => t.Id);

        builder.Property(t => t.TokenHash)
            .HasMaxLength(PasswordResetToken.TokenHashMaxLength)
            .IsRequired();

        // Único: el token se busca por su hash (el valor en claro no se guarda) y dos filas con el mismo hash harían ambigua la búsqueda al restablecer
        // (spec 018 R3).
        builder.HasIndex(t => t.TokenHash).IsUnique();

        // «¿Cuántos tokens se crearon para esta cuenta en la última hora?» (R4) y «anular los vigentes de esta cuenta» (R3, R9b): por cuenta y por fecha.
        builder.HasIndex(t => new { t.UserId, t.CreatedAt });

        // La purga (R18) borra por rango de caducidad: «los que caducaron hace más de N días». Sin este índice cada ciclo leería la tabla entera.
        builder.HasIndex(t => t.ExpiresAt);

        // Control de concurrencia optimista (R10): dos restablecimientos simultáneos con el mismo token leen la misma versión; el segundo en guardar
        // encuentra que ya cambió y falla, en vez de cambiar la contraseña dos veces. Un «if» en el código no lo garantiza: sería comprobar y escribir en dos pasos.
        builder.Property<byte[]>(RowVersion).IsRowVersion();

        // Deliberadamente SIN clave foránea hacia las credenciales: la tabla de credenciales es de Identity (ApplicationUser) y esta es de bookkeeping; la fila se
        // borra por caducidad (R18) y al dar de baja a una cuenta (ver DeleteUserAsync), no por cascada. Y sin ninguna columna con el email: solo el Id.
    }
}
