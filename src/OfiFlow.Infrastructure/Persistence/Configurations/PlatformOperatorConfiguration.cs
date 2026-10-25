using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OfiFlow.Infrastructure.Identity;

namespace OfiFlow.Infrastructure.Persistence.Configurations;

public sealed class PlatformOperatorConfiguration : IEntityTypeConfiguration<PlatformOperator>
{
    public void Configure(EntityTypeBuilder<PlatformOperator> builder)
    {
        // La cuenta es la clave: no puede haber dos filas de operador para la misma cuenta.
        builder.HasKey(o => o.UserId);
        builder.Property(o => o.UserId).ValueGeneratedNever();
    }
}
