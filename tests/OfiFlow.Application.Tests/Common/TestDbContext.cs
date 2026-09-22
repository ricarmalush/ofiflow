using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Jobs;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Common;

/// <summary>
/// Minimal EF Core InMemory-backed fake of IApplicationDbContext, for Application-layer
/// Handler tests only — not a substitute for the real Infrastructure.Tests integration tests.
/// </summary>
public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options), IApplicationDbContext
{
    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<TenantUser> TenantUsers => Set<TenantUser>();

    public DbSet<Job> Jobs => Set<Job>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(builder =>
        {
            builder.Property(c => c.Email)
                .HasConversion(email => email == null ? null : email.Value, value => value == null ? null : Email.Create(value));

            builder.Property(c => c.Phone)
                .HasConversion(phone => phone == null ? null : phone.Value, value => value == null ? null : PhoneNumber.Create(value));
        });

        modelBuilder.Entity<User>(builder =>
        {
            builder.Property(u => u.ContactEmail)
                .HasConversion(email => email.Value, value => Email.Create(value));
        });
    }
}
