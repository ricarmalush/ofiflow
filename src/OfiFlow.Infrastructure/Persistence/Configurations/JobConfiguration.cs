using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Infrastructure.Persistence.Configurations;

public sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.HasKey(j => j.Id);

        builder.Property(j => j.Title).IsRequired().HasMaxLength(200);
        builder.Property(j => j.Description).HasMaxLength(2000);

        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(j => j.Priority).HasConversion<string>().HasMaxLength(20);

        builder.HasIndex(j => j.CustomerId);
        builder.HasIndex(j => j.AssignedTenantUserId);
    }
}
