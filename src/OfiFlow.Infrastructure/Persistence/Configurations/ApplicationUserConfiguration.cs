using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OfiFlow.Infrastructure.Identity;

namespace OfiFlow.Infrastructure.Persistence.Configurations;

public sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.HasKey(u => u.Id);

        builder.Property(u => u.Email).HasMaxLength(320).IsRequired();
        builder.Property(u => u.NormalizedEmail).HasMaxLength(320);
        builder.HasIndex(u => u.NormalizedEmail).IsUnique();

        builder.Property(u => u.UserName).HasMaxLength(320);
        builder.Property(u => u.NormalizedUserName).HasMaxLength(320);

        builder.Property(u => u.PasswordHash).HasMaxLength(500);
        builder.Property(u => u.SecurityStamp).HasMaxLength(200);
        builder.Property(u => u.ConcurrencyStamp).HasMaxLength(200);
    }
}
