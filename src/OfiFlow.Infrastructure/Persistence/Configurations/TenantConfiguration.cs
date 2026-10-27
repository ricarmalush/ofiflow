using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Infrastructure.Persistence.Configurations;

public sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name)
            .IsRequired()
            .HasMaxLength(Tenant.NameMaxLength);

        // Nulo = empresa activa (spec 011). Sin valor por defecto ni índice: se lee junto a la empresa, por su clave.
        builder.Property(t => t.SuspendedAt);

        // Versión de fila (spec 011 bloque 6): dos suspensiones (o reactivaciones) simultáneas de la misma empresa no pueden
        // confirmarse las dos, o quedarían dos filas de auditoría para una sola suspensión. Gana una; la otra recibe el conflicto.
        builder.Property<byte[]>(InvitationConfiguration.RowVersion).IsRowVersion();
    }
}
