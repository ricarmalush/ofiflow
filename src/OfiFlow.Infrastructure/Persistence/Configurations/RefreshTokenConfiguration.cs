using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OfiFlow.Infrastructure.Identity;

namespace OfiFlow.Infrastructure.Persistence.Configurations;

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.HasKey(rt => rt.Id);

        builder.Property(rt => rt.TokenHash).HasMaxLength(200).IsRequired();

        builder.HasIndex(rt => rt.TokenHash).IsUnique();
        builder.HasIndex(rt => rt.UserId);

        // La consulta de «¿sigue activa esta sesión?» (spec 015) se hace en cada petición autenticada: busca por SessionId y mira
        // UserId, RevokedAt y ExpiresAt. Con esas tres columnas incluidas el índice la responde entero, sin leer la tabla.
        builder.HasIndex(rt => rt.SessionId).IncludeProperties(rt => new { rt.UserId, rt.RevokedAt, rt.ExpiresAt });
    }
}
