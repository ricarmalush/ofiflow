using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Company;
using OfiFlow.Domain.Customers;

namespace OfiFlow.Infrastructure.Persistence.Configurations;

public sealed class CompanyProfileConfiguration : IEntityTypeConfiguration<CompanyProfile>
{
    public void Configure(EntityTypeBuilder<CompanyProfile> builder)
    {
        builder.HasKey(p => p.Id);

        // Longitudes desde las constantes del dominio: la misma fuente que los validadores (ADR-009 R3).
        builder.Property(p => p.LegalName).IsRequired().HasMaxLength(CompanyProfile.LegalNameMaxLength);

        // El NIF se guarda normalizado y nunca se recorta: `Create` ya lo validó, así que leer una fila nunca falla por su forma.
        builder.Property(p => p.TaxId)
            .IsRequired()
            .HasConversion(taxId => taxId.Value, value => SpanishTaxId.Create(value))
            .HasMaxLength(SpanishTaxId.MaxLength);

        builder.Property(p => p.AddressLine).IsRequired().HasMaxLength(CompanyProfile.AddressLineMaxLength);

        builder.Property(p => p.PostalCode)
            .IsRequired()
            .HasConversion(code => code.Value, value => SpanishPostalCode.Create(value))
            .HasMaxLength(SpanishPostalCode.Length);

        builder.Property(p => p.City).IsRequired().HasMaxLength(CompanyProfile.CityMaxLength);
        builder.Property(p => p.Province).HasMaxLength(CompanyProfile.ProvinceMaxLength);

        builder.Property(p => p.Phone)
            .HasConversion(
                phone => phone == null ? null : phone.Value,
                value => value == null ? null : PhoneNumber.Create(value))
            .HasMaxLength(PhoneNumber.MaxLength);

        builder.Property(p => p.Email)
            .HasConversion(
                email => email == null ? null : email.Value,
                value => value == null ? null : Email.Create(value))
            .HasMaxLength(Email.MaxLength);

        // Los instantes se guardan siempre en UTC y SQL Server los devuelve sin zona: se leen marcados como UTC (spec 020, ADR-022).
        builder.Property(p => p.CreatedAt).HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        builder.Property(p => p.UpdatedAt).HasConversion(value => value, value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value);

        // Uno como mucho por empresa (R1): dos altas simultáneas no pueden dejar dos perfiles; la perdedora relee y modifica.
        builder.HasIndex(p => p.TenantId).IsUnique();
    }
}
