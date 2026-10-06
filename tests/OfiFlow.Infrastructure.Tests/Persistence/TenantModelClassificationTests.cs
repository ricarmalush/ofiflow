using Microsoft.EntityFrameworkCore;
using OfiFlow.Domain.Common;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Persistence;

/// <summary>
/// Guardarraíles de la spec 007 sobre el modelo de EF Core: ninguna entidad puede quedar sin
/// clasificar ("de una empresa" o "global") ni sin su filtro de tenant. Usan el proveedor en
/// memoria porque solo inspeccionan el modelo: no necesitan Docker.
/// </summary>
public class TenantModelClassificationTests
{
    private static readonly IReadOnlyDictionary<Type, string> Globals = TenantModelRules.GlobalEntities;

    private static Microsoft.EntityFrameworkCore.Metadata.IModel RealModel()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var context = new ApplicationDbContext(options, new FixedTenantContext(Guid.NewGuid()));
        return context.Model;
    }

    // --- El modelo real cumple las reglas ---

    [Fact]
    public void EveryMappedEntity_IsTenantOwnedOrDeclaredGlobal()
    {
        var violations = TenantModelRules.Unclassified(RealModel(), Globals);

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void EveryEntityWithTenantId_ImplementsTenantOwnedUnlessGlobal()
    {
        var violations = TenantModelRules.TenantIdWithoutMarker(RealModel(), Globals);

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void EveryTenantOwnedEntity_HasTheQueryFilterApplied()
    {
        var violations = TenantModelRules.OwnedWithoutFilter(RealModel());

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void GlobalEntitiesList_HasNoStaleOrContradictoryEntries()
    {
        var violations = TenantModelRules.StaleGlobals(RealModel(), Globals);

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void TheTenantOwnedEntitiesAreTheExpectedOnes()
    {
        // Red de seguridad contra un fallo silencioso: si la regla "toda entidad ITenantOwned tiene
        // filtro" se cumpliera por no haber ninguna entidad de empresa, los tests de arriba pasarían
        // sin comprobar nada. Se fija el conjunto actual; cambiarlo exige tocar este test a propósito.
        var owned = RealModel().GetEntityTypes()
            .Where(entity => typeof(ITenantOwned).IsAssignableFrom(entity.ClrType))
            .Select(entity => entity.ClrType.Name)
            .Order()
            .ToArray();

        Assert.Equal(["Customer", "Job", "TenantUser"], owned);
    }

    // --- Las reglas muerden: sobre un modelo con entidades mal clasificadas deben fallar ---

    [Fact]
    public void Rules_ReportAnEntityThatIsNeitherOwnedNorGlobal()
    {
        var violations = TenantModelRules.Unclassified(RogueModel(), Globals);

        Assert.Contains(violations, v => v.StartsWith(nameof(RogueUnclassified)));
        Assert.Contains(violations, v => v.StartsWith(nameof(RogueTenantIdWithoutMarker)));
    }

    [Fact]
    public void Rules_ReportAnEntityWithTenantIdButWithoutTheMarker()
    {
        var violations = TenantModelRules.TenantIdWithoutMarker(RogueModel(), Globals);

        Assert.Contains(violations, v => v.StartsWith(nameof(RogueTenantIdWithoutMarker)));
        Assert.DoesNotContain(violations, v => v.StartsWith(nameof(RogueUnclassified)));
    }

    [Fact]
    public void Rules_ReportATenantOwnedEntityWithoutQueryFilter()
    {
        var violations = TenantModelRules.OwnedWithoutFilter(RogueModel());

        Assert.Contains(violations, v => v.StartsWith(nameof(RogueOwnedWithoutFilter)));
        Assert.DoesNotContain(violations, v => v.StartsWith(nameof(RogueOwnedWithFilter)));
    }

    [Fact]
    public void Rules_ReportGlobalEntriesThatAreStaleOrContradictory()
    {
        var globals = new Dictionary<Type, string>
        {
            [typeof(RogueNotMapped)] = "no está en el modelo",
            [typeof(RogueOwnedWithFilter)] = "es de una empresa: contradictorio"
        };

        var violations = TenantModelRules.StaleGlobals(RogueModel(), globals);

        Assert.Contains(violations, v => v.StartsWith(nameof(RogueNotMapped)));
        Assert.Contains(violations, v => v.StartsWith(nameof(RogueOwnedWithFilter)));
    }

    [Fact]
    public void Rules_AcceptADeclaredGlobalEntity()
    {
        var globals = new Dictionary<Type, string> { [typeof(RogueUnclassified)] = "motivo" };

        var violations = TenantModelRules.Unclassified(RogueModel(), globals);

        Assert.DoesNotContain(violations, v => v.StartsWith(nameof(RogueUnclassified)));
    }

    private static Microsoft.EntityFrameworkCore.Metadata.IModel RogueModel()
    {
        var options = new DbContextOptionsBuilder<RogueDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var context = new RogueDbContext(options);
        return context.Model;
    }

    // Entidades de prueba, mal clasificadas a propósito. Solo existen en este fichero.
    private sealed class RogueUnclassified
    {
        public Guid Id { get; set; }
    }

    private sealed class RogueTenantIdWithoutMarker
    {
        public Guid Id { get; set; }

        public Guid TenantId { get; set; }
    }

    private sealed class RogueOwnedWithoutFilter : ITenantOwned
    {
        public Guid Id { get; set; }

        public Guid TenantId { get; set; }
    }

    private sealed class RogueOwnedWithFilter : ITenantOwned
    {
        public Guid Id { get; set; }

        public Guid TenantId { get; set; }
    }

    private sealed class RogueNotMapped
    {
        public Guid Id { get; set; }
    }

    private sealed class RogueDbContext(DbContextOptions<RogueDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<RogueUnclassified>();
            modelBuilder.Entity<RogueTenantIdWithoutMarker>();
            modelBuilder.Entity<RogueOwnedWithoutFilter>();
            modelBuilder.Entity<RogueOwnedWithFilter>().HasQueryFilter(entity => entity.TenantId == Guid.Empty);
        }
    }
}
