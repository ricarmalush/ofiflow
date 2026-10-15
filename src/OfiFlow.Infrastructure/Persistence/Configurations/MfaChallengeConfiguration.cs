using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OfiFlow.Infrastructure.Identity;

namespace OfiFlow.Infrastructure.Persistence.Configurations;

public sealed class MfaChallengeConfiguration : IEntityTypeConfiguration<MfaChallenge>
{
    public void Configure(EntityTypeBuilder<MfaChallenge> builder)
    {
        builder.HasKey(c => c.Id);

        builder.Property(c => c.TokenHash).HasMaxLength(MfaChallenge.TokenHashMaxLength).IsRequired();

        // Único: el desafío se busca por el hash de su valor (el valor en claro no se guarda).
        builder.HasIndex(c => c.TokenHash).IsUnique();

        // Para borrar los desafíos caducados de una cuenta al crear uno nuevo, y al borrar la cuenta.
        builder.HasIndex(c => c.UserId);

        // Dos canjes simultáneos del mismo desafío: solo uno puede consumirlo (spec 010 R11).
        builder.Property<byte[]>(InvitationConfiguration.RowVersion).IsRowVersion();
    }
}
