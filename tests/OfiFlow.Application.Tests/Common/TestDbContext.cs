using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Company;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Jobs;
using OfiFlow.Domain.Quotes;
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

    public DbSet<Invitation> Invitations => Set<Invitation>();

    public DbSet<Quote> Quotes => Set<Quote>();

    public DbSet<CompanyProfile> CompanyProfiles => Set<CompanyProfile>();

    /// <summary>Si tiene valor, el siguiente SaveChanges lanza esta excepción (para simular, por ejemplo, un conflicto de concurrencia).</summary>
    public Exception? ExceptionOnSave { get; set; }

    /// <summary>Las siguientes confirmaciones fallan con esta excepción, una por cada elemento de la cola (para simular, por ejemplo, una carrera por el número de un presupuesto que se resuelve al reintentar).</summary>
    public Queue<Exception> ExceptionsOnNextSaves { get; } = new();

    /// <summary>Se ejecuta justo antes de fallar con una excepción de la cola: para simular que otra petición confirmó algo entre medias (el perfil fiscal que gana una carrera, por ejemplo).</summary>
    public Action? OnQueuedFailure { get; set; }

    /// <summary>Cuántas veces se ha pedido guardar, para comprobar que una operación «todo o nada» confirma una sola vez (o ninguna).</summary>
    public int SaveCount { get; private set; }

    /// <summary>Pone el contador a cero: la preparación del test (sembrar datos) no cuenta como lo que hace la operación probada.</summary>
    public void ResetSaveCount() => SaveCount = 0;

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;

        if (ExceptionsOnNextSaves.TryDequeue(out var queued))
        {
            OnQueuedFailure?.Invoke();
            return Task.FromException<int>(queued);
        }

        return ExceptionOnSave is { } exception ? Task.FromException<int>(exception) : base.SaveChangesAsync(cancellationToken);
    }

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

        // Igual que en la configuración real de las líneas de un presupuesto (QuoteItemConfiguration): su clave la genera el dominio.
        modelBuilder.Entity<QuoteItem>(builder => builder.Property(i => i.Id).ValueGeneratedNever());

        // Igual que en la configuración real del perfil fiscal (CompanyProfileConfiguration): los objetos de valor se guardan como texto.
        modelBuilder.Entity<CompanyProfile>(builder =>
        {
            builder.Property(p => p.TaxId).HasConversion(taxId => taxId.Value, value => SpanishTaxId.Create(value));
            builder.Property(p => p.PostalCode).HasConversion(code => code.Value, value => SpanishPostalCode.Create(value));
            builder.Property(p => p.Phone).HasConversion(phone => phone == null ? null : phone.Value, value => value == null ? null : PhoneNumber.Create(value));
            builder.Property(p => p.Email).HasConversion(email => email == null ? null : email.Value, value => value == null ? null : Email.Create(value));
        });

        modelBuilder.Entity<Invitation>(builder =>
        {
            builder.Property(i => i.Email)
                .HasConversion(email => email.Value, value => Email.Create(value));
        });
    }
}
