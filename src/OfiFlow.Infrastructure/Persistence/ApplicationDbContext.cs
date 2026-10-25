using System.Reflection;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Jobs;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Identity;

namespace OfiFlow.Infrastructure.Persistence;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, ITenantContext tenantContext)
    : DbContext(options), IApplicationDbContext
{
    private static readonly MethodInfo SetTenantQueryFilterMethod = typeof(ApplicationDbContext)
        .GetMethod(nameof(SetTenantQueryFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<TenantUser> TenantUsers => Set<TenantUser>();

    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    /// <summary>
    /// Not part of IApplicationDbContext on purpose — only IIdentityService/ITokenService
    /// (Infrastructure) touch credentials/tokens directly (ADR-007).
    /// </summary>
    internal DbSet<ApplicationUser> ApplicationUsers => Set<ApplicationUser>();

    internal DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    /// <summary>El segundo factor (spec 010): tampoco forma parte de IApplicationDbContext, solo lo toca Infrastructure.</summary>
    internal DbSet<MfaCredential> MfaCredentials => Set<MfaCredential>();

    internal DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();

    internal DbSet<MfaChallenge> MfaChallenges => Set<MfaChallenge>();

    /// <summary>
    /// Los operadores de la plataforma y su auditoría (spec 011, ADR-016): globales, no de una empresa, y solo los toca
    /// Infrastructure (<see cref="PlatformOperators"/> y <see cref="PlatformAudit"/>).
    /// </summary>
    internal DbSet<PlatformOperator> PlatformOperators => Set<PlatformOperator>();

    internal DbSet<PlatformAuditEntry> PlatformAuditEntries => Set<PlatformAuditEntry>();

    // La auditoría de plataforma es de SOLO inserción (spec 011 R17): una auditoría que se puede reescribir no prueba nada.
    // Se comprueba aquí, en el propio contexto, y no en un interceptor registrado aparte, para que valga con CUALQUIER forma
    // de construirlo (la API, los tests, una herramienta): no hay un camino que se la salte por olvidar registrar algo.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureThePlatformAuditIsAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureThePlatformAuditIsAppendOnly();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnsureThePlatformAuditIsAppendOnly()
    {
        if (ChangeTracker.Entries<PlatformAuditEntry>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("La auditoría de plataforma es de solo inserción: no se modifica ni se borra (spec 011 R17).");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        ApplyTenantQueryFilters(modelBuilder);
    }

    /// <summary>
    /// Configures the Global Query Filter once, by reflection, for every entity that
    /// implements ITenantOwned — no entity is exempt by omission (ADR-002/006).
    /// </summary>
    private void ApplyTenantQueryFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
            {
                SetTenantQueryFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
            }
        }
    }

    private void SetTenantQueryFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantOwned
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(entity => entity.TenantId == tenantContext.TenantId);
    }
}
