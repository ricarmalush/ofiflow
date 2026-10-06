using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Identity;

namespace OfiFlow.Infrastructure.Tests.Persistence;

/// <summary>
/// Reglas de aislamiento sobre el modelo de EF Core (spec 007, ADR-009 R2). Cada regla devuelve
/// la lista de incumplimientos, vacía si se cumple, con un mensaje que explica qué hacer.
/// Se aplican al modelo real (lo que de verdad se mapea) y no a un escaneo de código, así que
/// no se pueden esquivar cambiando el nombre de una variable.
/// </summary>
internal static class TenantModelRules
{
    private const string TenantIdProperty = "TenantId";

    /// <summary>
    /// Entidades que NO pertenecen a una empresa, cada una con su motivo. Añadir una aquí es una
    /// decisión consciente: significa que no tiene filtro de tenant y cualquier consulta sobre
    /// ella ve los datos de todas las empresas.
    /// </summary>
    public static readonly IReadOnlyDictionary<Type, string> GlobalEntities = new Dictionary<Type, string>
    {
        [typeof(Tenant)] = "es la propia empresa",
        [typeof(User)] = "una persona puede pertenecer a varias empresas (ADR-004)",
        [typeof(ApplicationUser)] = "credenciales de una persona, no de una empresa (ADR-007)",
        [typeof(RefreshToken)] = "guarda el TenantId de la sesión, pero solo se busca por el hash del token (ADR-007)"
    };

    /// <summary>R1: toda entidad mapeada es de una empresa o está declarada como global.</summary>
    public static IReadOnlyList<string> Unclassified(IModel model, IReadOnlyDictionary<Type, string> globals) =>
        Entities(model)
            .Where(entity => !IsTenantOwned(entity) && !globals.ContainsKey(entity.ClrType))
            .Select(entity =>
                $"{entity.ClrType.Name}: no implementa ITenantOwned ni está en la lista de entidades globales. " +
                "Si es de una empresa, implementa ITenantOwned. Si es global, añádela a TenantModelRules.GlobalEntities con su motivo.")
            .ToList();

    /// <summary>R1: una entidad con propiedad TenantId debe llevar la marca, salvo que sea global.</summary>
    public static IReadOnlyList<string> TenantIdWithoutMarker(IModel model, IReadOnlyDictionary<Type, string> globals) =>
        Entities(model)
            .Where(entity => entity.FindProperty(TenantIdProperty) is not null)
            .Where(entity => !IsTenantOwned(entity) && !globals.ContainsKey(entity.ClrType))
            .Select(entity =>
                $"{entity.ClrType.Name}: tiene una propiedad TenantId pero no implementa ITenantOwned, así que no tiene " +
                "filtro de tenant y sus datos serían visibles entre empresas. Implementa ITenantOwned.")
            .ToList();

    /// <summary>R1: toda entidad de una empresa tiene el filtro aplicado en el modelo.</summary>
    public static IReadOnlyList<string> OwnedWithoutFilter(IModel model) =>
        Entities(model)
            .Where(IsTenantOwned)
            .Where(entity => entity.GetDeclaredQueryFilters().Count == 0)
            .Select(entity =>
                $"{entity.ClrType.Name}: implementa ITenantOwned pero no tiene filtro de consulta en el modelo. " +
                "Revisa ApplicationDbContext.ApplyTenantQueryFilters.")
            .ToList();

    /// <summary>La lista de globales no puede quedarse con entradas obsoletas ni contradictorias.</summary>
    public static IReadOnlyList<string> StaleGlobals(IModel model, IReadOnlyDictionary<Type, string> globals)
    {
        var mapped = Entities(model).Select(entity => entity.ClrType).ToHashSet();

        return globals.Keys
            .Where(type => !mapped.Contains(type) || typeof(ITenantOwned).IsAssignableFrom(type))
            .Select(type =>
                $"{type.Name}: está en la lista de entidades globales pero no se mapea, o implementa ITenantOwned. " +
                "Quítala de TenantModelRules.GlobalEntities.")
            .ToList();
    }

    // Los tipos "owned" son parte de otra entidad (no tienen tabla propia ni filtro propio).
    private static IEnumerable<IReadOnlyEntityType> Entities(IModel model) =>
        model.GetEntityTypes().Where(entity => !entity.IsOwned());

    private static bool IsTenantOwned(IReadOnlyEntityType entity) =>
        typeof(ITenantOwned).IsAssignableFrom(entity.ClrType);
}
