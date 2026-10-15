using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OfiFlow.Infrastructure.Identity;

namespace OfiFlow.Infrastructure.Persistence.Configurations;

public sealed class RecoveryCodeConfiguration : IEntityTypeConfiguration<RecoveryCode>
{
    public void Configure(EntityTypeBuilder<RecoveryCode> builder)
    {
        builder.HasKey(c => c.Id);

        builder.Property(c => c.CodeHash).HasMaxLength(RecoveryCode.CodeHashMaxLength).IsRequired();

        // Único: el código se busca por su hash, y dos filas con el mismo hash harían ambigua la búsqueda.
        builder.HasIndex(c => c.CodeHash).IsUnique();

        // Para borrar o contar los códigos de una cuenta.
        builder.HasIndex(c => c.UserId);

        // Dos usos simultáneos del mismo código: solo uno puede consumirlo (spec 010 R9).
        builder.Property<byte[]>(InvitationConfiguration.RowVersion).IsRowVersion();
    }
}
