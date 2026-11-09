using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Infrastructure.Persistence.Configurations;

public sealed class QuoteConfiguration : IEntityTypeConfiguration<Quote>
{
    public void Configure(EntityTypeBuilder<Quote> builder)
    {
        builder.HasKey(q => q.Id);

        // Longitudes desde las constantes del dominio: la misma fuente que los validadores (ADR-009 R3).
        builder.Property(q => q.Title).IsRequired().HasMaxLength(Quote.TitleMaxLength);
        builder.Property(q => q.Notes).HasMaxLength(Quote.NotesMaxLength);

        builder.Property(q => q.Status).HasConversion<string>().HasMaxLength(20);

        // Una fecha de calendario, sin hora ni huso (spec 023 D8): columna `date`.
        builder.Property(q => q.ValidUntil).HasColumnType("date");

        // Los instantes se guardan siempre en UTC y SQL Server los devuelve sin zona: se leen marcados como UTC para que el JSON lleve la «Z» (spec 020, ADR-022).
        builder.Property(q => q.CreatedAt).HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        builder.Property(q => q.UpdatedAt).HasConversion(value => value, value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value);
        builder.Property(q => q.SentAt).HasConversion(value => value, value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value);
        builder.Property(q => q.DecidedAt).HasConversion(value => value, value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value);

        // Control de concurrencia optimista (D9, R10): la versión de fila la rellena SQL Server y viaja en el WHERE de cada UPDATE y DELETE.
        builder.Property(q => q.RowVersion).IsRowVersion();

        // Las líneas son parte del agregado: se borran con su presupuesto y se leen con Include. El campo privado es el que EF rellena.
        builder.HasMany(q => q.Items).WithOne().HasForeignKey(i => i.QuoteId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(q => q.Items).HasField("_items").UsePropertyAccessMode(PropertyAccessMode.Field);

        // El número es único por empresa (R6): dos creaciones simultáneas no pueden repetirlo; la perdedora reintenta.
        builder.HasIndex(q => new { q.TenantId, q.Number }).IsUnique();

        // Los filtros del listado (R15): TenantId primero, que es por lo que filtra siempre el filtro global.
        builder.HasIndex(q => new { q.TenantId, q.CustomerId });
        builder.HasIndex(q => new { q.TenantId, q.Status, q.CreatedAt }).IsDescending(false, false, true);
    }
}
