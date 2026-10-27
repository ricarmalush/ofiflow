using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OfiFlow.Application.Platform;
using OfiFlow.Infrastructure.Identity;

namespace OfiFlow.Infrastructure.Persistence.Configurations;

public sealed class PlatformAuditEntryConfiguration : IEntityTypeConfiguration<PlatformAuditEntry>
{
    public void Configure(EntityTypeBuilder<PlatformAuditEntry> builder)
    {
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Action).HasMaxLength(PlatformAuditActions.MaxLength).IsRequired();

        // «¿Qué le hicieron a esta empresa?» y «¿qué hizo este operador?»: las dos preguntas que justifican la tabla.
        builder.HasIndex(e => e.TenantId);
        builder.HasIndex(e => new { e.OperatorUserId, e.OccurredAt });

        // Sin RowVersion a propósito: solo se inserta, nadie la actualiza.
    }
}
