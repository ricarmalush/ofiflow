using Microsoft.EntityFrameworkCore;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Common.Persistence;

/// <summary>
/// Abstracts EF Core away from Domain/Application (ADR-006) — implemented by the real
/// ApplicationDbContext in Infrastructure. No generic repository on top of it.
/// </summary>
public interface IApplicationDbContext
{
    DbSet<Customer> Customers { get; }

    DbSet<Tenant> Tenants { get; }

    DbSet<User> Users { get; }

    DbSet<TenantUser> TenantUsers { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
