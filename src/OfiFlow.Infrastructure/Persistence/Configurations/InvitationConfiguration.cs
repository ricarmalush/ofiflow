using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Infrastructure.Persistence.Configurations;

public sealed class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    /// <summary>Nombre de la columna de concurrencia. Es una propiedad "sombra": Domain no sabe que existe.</summary>
    public const string RowVersion = "RowVersion";

    public void Configure(EntityTypeBuilder<Invitation> builder)
    {
        builder.HasKey(i => i.Id);

        builder.Property(i => i.Email)
            .HasConversion(email => email.Value, value => Email.Create(value))
            .HasMaxLength(Email.MaxLength)
            .IsRequired();

        builder.Property(i => i.NormalizedEmail)
            .HasMaxLength(Email.MaxLength)
            .IsRequired();

        builder.Property(i => i.Role)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(i => i.TokenHash)
            .HasMaxLength(Invitation.TokenHashMaxLength)
            .IsRequired();

        // Único: el token se busca por su hash (el valor en claro no se guarda) y dos invitaciones
        // con el mismo hash harían ambigua la búsqueda al aceptar (spec 009 R2).
        builder.HasIndex(i => i.TokenHash).IsUnique();

        // Para la regla "una pendiente por (empresa, email)" y el recuento de pendientes (R5).
        builder.HasIndex(i => new { i.TenantId, i.NormalizedEmail });

        // Control de concurrencia optimista (spec 009 R8): dos aceptaciones simultáneas del mismo token
        // leen la misma versión; la segunda en guardar encuentra que ya cambió y falla, en vez de crear
        // dos cuentas. Un "if" en el código no lo garantiza: la comprobación y la escritura serían dos pasos.
        builder.Property<byte[]>(RowVersion).IsRowVersion();
    }
}
