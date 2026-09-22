using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Infrastructure.Persistence.Configurations;

public sealed class TenantUserConfiguration : IEntityTypeConfiguration<TenantUser>
{
    public void Configure(EntityTypeBuilder<TenantUser> builder)
    {
        builder.HasKey(tu => tu.Id);

        builder.Property(tu => tu.Role)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.HasIndex(tu => new { tu.TenantId, tu.UserId }).IsUnique();
    }
}
