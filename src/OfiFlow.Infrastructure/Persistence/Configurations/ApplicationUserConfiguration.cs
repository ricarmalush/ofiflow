using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OfiFlow.Domain.Common;
using OfiFlow.Infrastructure.Identity;

namespace OfiFlow.Infrastructure.Persistence.Configurations;

public sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.HasKey(u => u.Id);

        builder.Property(u => u.Email).HasMaxLength(Email.MaxLength).IsRequired();
        builder.Property(u => u.NormalizedEmail).HasMaxLength(Email.MaxLength);
        builder.HasIndex(u => u.NormalizedEmail).IsUnique();

        builder.Property(u => u.UserName).HasMaxLength(Email.MaxLength);
        builder.Property(u => u.NormalizedUserName).HasMaxLength(Email.MaxLength);

        builder.Property(u => u.PasswordHash).HasMaxLength(500);
        builder.Property(u => u.SecurityStamp).HasMaxLength(200);
        builder.Property(u => u.ConcurrencyStamp).HasMaxLength(200);
    }
}
