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
    }
}
