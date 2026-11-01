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

        // Los datos de la sesión (spec 016): el navegador recortado y la IP truncada. Las longitudes son las de los saneadores de ClientInfo, que recortan antes de guardar.
        builder.Property(rt => rt.UserAgent).HasMaxLength(ClientInfo.MaxUserAgentLength);
        builder.Property(rt => rt.IpAddress).HasMaxLength(ClientInfo.MaxIpLength);

        builder.HasIndex(rt => rt.TokenHash).IsUnique();
        builder.HasIndex(rt => rt.UserId);

        // La consulta de «¿sigue activa esta sesión?» (spec 015) se hace en cada petición autenticada: busca por SessionId y mira
        // UserId, RevokedAt y ExpiresAt. Con esas tres columnas incluidas el índice la responde entero, sin leer la tabla.
        builder.HasIndex(rt => rt.SessionId).IncludeProperties(rt => new { rt.UserId, rt.RevokedAt, rt.ExpiresAt });

        // La purga (spec 016) borra por rango de caducidad: «las que caducaron hace más de N días». Sin este índice cada ciclo leería la tabla
        // entera, que se consulta en cada petición (spec 015). Con él, el borrado recorre solo el principio del índice.
        builder.HasIndex(rt => rt.ExpiresAt);
    }
}
