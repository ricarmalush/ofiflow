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
        // aceptar una invitación: la persona aún no tiene cuenta ni empresa activa, la invitación se busca por el hash de su token (ADR-013)
        "src/OfiFlow.Application/Identity/Commands/AcceptInvitation/AcceptInvitationCommandHandler.cs",
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
        // la ficha de una persona solo se lee desde las pertenencias de la empresa activa: listar los miembros (join con
        // TenantUsers, que sí está filtrada) y quitar a uno (se busca a partir de su pertenencia). Spec 009.
        ["Users"] =
        [
            "src/OfiFlow.Application/Tenancy/Queries/GetUsers/GetUsersQueryHandler.cs",
            "src/OfiFlow.Application/Tenancy/Commands/RemoveUser/RemoveUserCommandHandler.cs"
        ],
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

    /// <summary>
    /// El único sitio donde se usa HMAC-SHA1 y se suprime el aviso CA5350: el TOTP, porque la RFC 6238 lo exige
    /// (ADR-014). Un uso nuevo de SHA-1 en cualquier otro sitio tiene que justificarse y añadirse aquí a propósito.
    /// </summary>
    private const string WeakAlgorithmAllowedFile = "src/OfiFlow.Application/Identity/Mfa/Totp.cs";

    [Theory]
    [InlineData("HMACSHA1")]
    [InlineData("CA5350")]
    public void Sha1_IsOnlyUsedByTheTotpImplementation(string marker)
    {
        var offenders = SourceCode.FilesContaining(marker).Where(path => path != WeakAlgorithmAllowedFile).ToList();

        Assert.True(offenders.Count == 0,
            $"'{marker}' solo puede aparecer en {WeakAlgorithmAllowedFile} (ADR-014: HMAC-SHA1 es lo que exige el TOTP; " +
            $"para cualquier otra cosa se usa SHA-256 o más). Aparece en: {string.Join(", ", offenders)}");

        // Control positivo: el escaneo encuentra el uso legítimo. Si el fichero se moviera, este test avisaría
        // de que ya no vigila nada en vez de pasar en silencio.
        Assert.Contains(WeakAlgorithmAllowedFile, SourceCode.FilesContaining(marker));
    }

    [Fact]
    public void TheTotpCodeComparison_IsConstantTime()
    {
        // Comparar el código con == o SequenceEqual sale al primer carácter distinto, y el tiempo que tarda
        // enseñaría cuántos dígitos se han acertado. FixedTimeEquals tarda lo mismo acierte lo que acierte
        // (spec 010 R4). Por comportamiento no se puede probar (es una cuestión de tiempo), así que se vigila la fuente.
        var totp = SourceCode.Files("OfiFlow.Application/Identity/Mfa").Single(file => file.Path.EndsWith("/Totp.cs", StringComparison.Ordinal));

        Assert.Contains("CryptographicOperations.FixedTimeEquals(", totp.Content);
        Assert.DoesNotContain("SequenceEqual", totp.Content);
        Assert.DoesNotContain("string.Equals", totp.Content);
        Assert.DoesNotContain("== code", totp.Content);
        Assert.DoesNotContain("== expected", totp.Content);
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
