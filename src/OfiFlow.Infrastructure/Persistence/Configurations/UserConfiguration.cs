using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasKey(u => u.Id);

        builder.Property(u => u.Name)
            .IsRequired()
            .HasMaxLength(User.NameMaxLength);

        builder.Property(u => u.ContactEmail)
            .HasConversion(email => email.Value, value => Domain.Common.Email.Create(value))
            .HasMaxLength(Domain.Common.Email.MaxLength)
            .IsRequired();

        // Las condiciones de uso aceptadas (spec 021 R16): nulas en las cuentas que ya existían (R19), que no cambian. Solo la etiqueta de la versión, nunca el texto.
        builder.Property(u => u.TermsVersion).HasMaxLength(User.TermsVersionMaxLength);
        builder.Property(u => u.TermsAcceptedAt);
    }
}
