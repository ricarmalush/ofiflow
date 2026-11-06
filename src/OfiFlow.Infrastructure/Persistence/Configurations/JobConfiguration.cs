using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Infrastructure.Persistence.Configurations;

public sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.HasKey(j => j.Id);

        builder.Property(j => j.Title).IsRequired().HasMaxLength(Job.TitleMaxLength);
        builder.Property(j => j.Description).HasMaxLength(Job.DescriptionMaxLength);

        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(j => j.Priority).HasConversion<string>().HasMaxLength(20);

        // SQL Server guarda un datetime2 sin zona y EF lo devuelve con Kind = Unspecified: al serializarlo saldría sin la «Z» y el navegador lo
        // leería como hora local (spec 020, ADR-022). Esta columna se guarda siempre en UTC (Job.Schedule lo exige), así que se lee marcada como
        // tal. Solo esta columna: el resto de fechas del sistema no cambian de comportamiento.
        builder.Property(j => j.ScheduledDate).HasConversion(
            value => value,
            value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value);

        builder.HasIndex(j => j.CustomerId);
        builder.HasIndex(j => j.AssignedTenantUserId);

        // La agenda pide los trabajos de una empresa dentro de un rango de fechas (spec 020 R11): TenantId primero, que es por
        // lo que filtra siempre el filtro global, y la fecha después, para el rango.
        builder.HasIndex(j => new { j.TenantId, j.ScheduledDate });
    }
}
