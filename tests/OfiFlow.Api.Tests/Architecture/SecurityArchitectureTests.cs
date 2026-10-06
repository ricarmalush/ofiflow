using MediatR;
using OfiFlow.Application;

namespace OfiFlow.Api.Tests.Architecture;

/// <summary>
/// Guardarraíles de ADR-009 (R1 y R2): convierten reglas de seguridad en tests que fallan si
/// alguien las incumple, en vez de depender de acordarse en cada revisión.
/// </summary>
public class SecurityArchitectureTests
{
    /// <summary>
    /// Únicos ficheros (por ruta relativa, no por nombre) autorizados a saltarse el Global Query
    /// Filter de tenant (ADR-002). Añadir uno nuevo exige justificarlo en el código y en ADR-009.
    /// </summary>
    private static readonly string[] IgnoreQueryFiltersAllowList =
    [
        // todavía no hay tenant activo: es lo que el login determina
        "src/OfiFlow.Application/Identity/Commands/Login/LoginCommandHandler.cs",
        // el refresh relee el rol del usuario en su tenant
        "src/OfiFlow.Infrastructure/Identity/TokenService.cs"
    ];

    /// <summary>
    /// DbSets de entidades globales (sin filtro de tenant: spec 007, lista en TenantModelRules de
    /// Infrastructure.Tests) y los únicos ficheros que pueden leerlos. Crear un dato (.Add) se admite en
    /// cualquier sitio. Un handler nuevo que lea Users o Tenants vería a todas las empresas, así que
    /// leerlos exige añadir aquí el fichero con su motivo.
    /// </summary>
    private static readonly Dictionary<string, string[]> GlobalDbSetReadAllowList = new()
    {
        ["Users"] = [],
        ["Tenants"] = [],
        // credenciales: se buscan por email o por Id de usuario, nunca por listado
        ["ApplicationUsers"] = ["src/OfiFlow.Infrastructure/Identity/IdentityService.cs"],
        // se buscan por el hash del token, que es secreto y único
        ["RefreshTokens"] = ["src/OfiFlow.Infrastructure/Identity/TokenService.cs"]
    };

    [Theory]
    [InlineData("FromSqlRaw")]
    [InlineData("ExecuteSqlRaw")]
    public void SourceCode_DoesNotUseRawSqlApis(string forbiddenApi)
    {
        var offenders = SourceCode.FilesContaining(forbiddenApi);

        Assert.True(offenders.Count == 0,
            $"'{forbiddenApi}' está prohibido (ADR-009 R1: riesgo de inyección SQL). " +
            $"Usa LINQ, o FromSql/SqlQuery con interpolación (parametrizado). Aparece en: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void IgnoreQueryFilters_IsOnlyUsedInTheAllowList()
    {
        var offenders = TenantAccessRules.IgnoreQueryFiltersOffenders(SourceCode.Files(), IgnoreQueryFiltersAllowList);

        Assert.True(offenders.Count == 0,
            "IgnoreQueryFilters() se salta el aislamiento de tenant (ADR-002, ADR-009 R2) y solo se permite en " +
            $"{string.Join(", ", IgnoreQueryFiltersAllowList)}. Aparece también en: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void GlobalEntities_AreOnlyReadInTheAllowList()
    {
        var offenders = TenantAccessRules.GlobalEntityAccessOffenders(SourceCode.Files(), GlobalDbSetReadAllowList);

        Assert.True(offenders.Count == 0,
            "Users, Tenants, ApplicationUsers y RefreshTokens no tienen filtro de tenant (spec 007, ADR-009 R2): " +
            "leerlos puede exponer datos de todas las empresas. Solo se leen en los ficheros de GlobalDbSetReadAllowList; " +
            $"para añadir uno, justifícalo ahí. Accesos no permitidos: {string.Join("; ", offenders)}");
    }

    [Fact]
    public void NoCommandOrQuery_AcceptsTenantIdFromTheClient()
    {
        var offenders = typeof(DependencyInjection).Assembly.GetTypes()
            .Where(type => type.GetInterfaces().Any(IsMediatRRequest))
            .Where(type => type.GetProperty("TenantId") is not null)
            .Select(type => type.FullName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Un Command/Query no puede recibir TenantId: se toma siempre del JWT vía ITenantContext " +
            $"(ADR-002, ADR-009 R2, protección contra mass assignment). Lo tienen: {string.Join(", ", offenders)}");
    }

    private static bool IsMediatRRequest(Type type) =>
        type == typeof(IRequest) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IRequest<>));

}
