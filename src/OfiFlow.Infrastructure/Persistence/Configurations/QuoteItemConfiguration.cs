using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Infrastructure.Persistence.Configurations;

public sealed class QuoteItemConfiguration : IEntityTypeConfiguration<QuoteItem>
{
    public void Configure(EntityTypeBuilder<QuoteItem> builder)
    {
        builder.ToTable("QuoteItems");

        builder.HasKey(i => i.Id);

        // La clave la genera el dominio (Guid.NewGuid en el constructor), no la base de datos. Sin esto EF trata una línea nueva con su Id ya puesto, al descubrirla por la
        // colección del presupuesto, como una línea que ya existe (la marca Modified): al sustituir las líneas intentaría un UPDATE y fallaría. Con ValueGeneratedNever la marca Added.
        builder.Property(i => i.Id).ValueGeneratedNever();

        builder.Property(i => i.Description).IsRequired().HasMaxLength(Quote.DescriptionMaxLength);

        // Hasta 3 decimales y 99 999,999 (R3): decimal(9,3) los cubre con margen.
        builder.Property(i => i.Quantity).HasPrecision(9, 3);

        // El neto de la línea no se guarda: se calcula (QuoteCalculator).
        builder.Ignore(i => i.NetCents);

        builder.HasIndex(i => new { i.QuoteId, i.Position });
    }
}
