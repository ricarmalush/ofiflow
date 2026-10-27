using MediatR;
using OfiFlow.Application;
using OfiFlow.Application.Common.Authorization;

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
        // canjear el desafío del login en dos pasos: la petición es anónima y aún no hay empresa activa; la pertenencia se busca por la cuenta del desafío (ADR-014)
        "src/OfiFlow.Application/Identity/Commands/VerifyMfaChallenge/VerifyMfaChallengeCommandHandler.cs",
        // el refresh relee el rol del usuario en su tenant
        "src/OfiFlow.Infrastructure/Identity/TokenService.cs",
        // dar de alta a un operador de la plataforma (spec 011 R1): hay que saber si la cuenta tiene ALGUNA pertenencia, en cualquier empresa, y no hay empresa activa; solo mira si existe una fila (AnyAsync), no la lee
        "src/OfiFlow.Infrastructure/Identity/PlatformOperators.cs",
        // listar las empresas (spec 011 R11): hay que contar los miembros de CADA empresa y el operador no tiene ninguna activa; es un recuento por empresa de la página, sin proyectar ninguna columna de personas
        "src/OfiFlow.Application/Platform/Queries/ListTenants/ListTenantsQueryHandler.cs"
    ];

    /// <summary>
    /// DbSets de entidades globales (sin filtro de tenant: spec 007, lista en TenantModelRules de
    /// Infrastructure.Tests) y los únicos ficheros que pueden leerlos. Crear un dato (.Add) se admite en
    /// cualquier sitio. Un handler nuevo que lea Users o Tenants vería a todas las empresas, así que
    /// leerlos exige añadir aquí el fichero con su motivo.
    /// </summary>
    private const string MfaServiceFile = "src/OfiFlow.Infrastructure/Identity/MfaService.cs";
    private const string MfaStoreFile = "src/OfiFlow.Infrastructure/Identity/MfaStore.cs";

    private static readonly Dictionary<string, string[]> GlobalDbSetReadAllowList = new()
    {
        // la ficha de una persona solo se lee desde las pertenencias de la empresa activa: listar los miembros (join con
        // TenantUsers, que sí está filtrada) y quitar a uno (se busca a partir de su pertenencia). Spec 009.
        ["Users"] =
        [
            "src/OfiFlow.Application/Tenancy/Queries/GetUsers/GetUsersQueryHandler.cs",
            // quién soy (spec 012): la ficha de la PROPIA persona, solo desde su pertenencia (filtrada por empresa)
            "src/OfiFlow.Application/Identity/Queries/GetCurrentAccount/GetCurrentAccountQueryHandler.cs",
            "src/OfiFlow.Application/Tenancy/Commands/RemoveUser/RemoveUserCommandHandler.cs"
        ],
        // el estado de suspensión de la empresa (spec 011): UNA sola lectura, reutilizada por el login, el canje del segundo
        // factor, el refresh, aceptar una invitación y la comprobación de pertenencia de cada petición. Un sitio nuevo que
        // necesite saber si una empresa está suspendida llama a TenantStatus; no lee la tabla por su cuenta.
        // Además, los tres handlers del panel de la plataforma (spec 011 R11 y R12): listar las empresas, suspender una y reactivarla.
        // Son las únicas operaciones que ven una empresa que no es la suya, y las tres exigen ser un operador activo.
        ["Tenants"] =
        [
            "src/OfiFlow.Application/Tenancy/TenantStatus.cs",
            "src/OfiFlow.Application/Platform/Queries/ListTenants/ListTenantsQueryHandler.cs",
            "src/OfiFlow.Application/Platform/Commands/SuspendTenant/SuspendTenantCommandHandler.cs",
            "src/OfiFlow.Application/Platform/Commands/ReactivateTenant/ReactivateTenantCommandHandler.cs",
            // quién soy (spec 012): el nombre de la PROPIA empresa, solo desde la pertenencia de quien pregunta (filtrada por empresa)
            "src/OfiFlow.Application/Identity/Queries/GetCurrentAccount/GetCurrentAccountQueryHandler.cs"
        ],
        // credenciales: se buscan por email o por Id de usuario, nunca por listado
        ["ApplicationUsers"] = [
            "src/OfiFlow.Infrastructure/Identity/IdentityService.cs",
            // el contador de intentos y el bloqueo se actualizan por el Id de la cuenta (spec 010)
            "src/OfiFlow.Infrastructure/Identity/AccountLockout.cs",
            // el email de la cuenta (nombre en la app del móvil) y la contraseña a reconfirmar, siempre por el Id de la cuenta (spec 010)
            MfaServiceFile
        ],
        // el segundo factor (spec 010): credenciales de una cuenta, siempre por su Id (comprobar, borrar al desactivar o al quitar a un miembro)
        ["MfaCredentials"] = [
            MfaServiceFile,
            MfaStoreFile,
            // el login consulta si la cuenta que se autentica tiene el segundo factor activo (siempre por su Id): decide si basta la contraseña (spec 010 R10)
            "src/OfiFlow.Infrastructure/Identity/IdentityService.cs"
        ],
        ["RecoveryCodes"] = [MfaServiceFile, MfaStoreFile],
        ["MfaChallenges"] = [MfaServiceFile, MfaStoreFile],
        // se buscan por el hash del token, que es secreto y único
        ["RefreshTokens"] = ["src/OfiFlow.Infrastructure/Identity/TokenService.cs"],
        // los operadores de la plataforma (spec 011): se consulta siempre por el Id de la cuenta ("¿es operador activo?", alta y baja), nunca por listado
        ["PlatformOperators"] = ["src/OfiFlow.Infrastructure/Identity/PlatformOperators.cs"],
        // la auditoría de plataforma es de solo inserción (.Add se admite en cualquier sitio) y hoy NADIE la lee desde la aplicación: una lectura nueva tiene que añadirse aquí a propósito
        ["PlatformAuditEntries"] = []
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
    /// El único fichero que ejecuta SQL fuera de LINQ: la reserva atómica del intento de login (spec 010 R18). Va
    /// parametrizado (interpolación), pero es una superficie de inyección SQL, así que se limita a una y se justifica.
    /// </summary>
    private const string RawSqlAllowedFile = "src/OfiFlow.Infrastructure/Identity/AccountLockout.cs";

    [Theory]
    [InlineData("SqlQuery<")]
    [InlineData("FromSql(")]
    [InlineData("FromSqlInterpolated")]
    [InlineData("ExecuteSql(")]
    [InlineData("ExecuteSqlAsync(")]
    [InlineData("ExecuteSqlInterpolated")]
    public void ParameterizedRawSql_IsOnlyUsedInTheLockout(string api)
    {
        var offenders = SourceCode.FilesContaining(api).Where(path => path != RawSqlAllowedFile).ToList();

        Assert.True(offenders.Count == 0,
            $"'{api}' ejecuta SQL fuera de LINQ y solo está permitido en {RawSqlAllowedFile} (ADR-014: la reserva atómica del " +
            $"intento). Un uso nuevo hay que justificarlo y añadirlo aquí a propósito. Aparece en: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// Los ÚNICOS sitios que crean una pertenencia a una empresa (<c>TenantUser</c>), y los dos con una cuenta <b>recién
    /// creada</b> (un <c>Guid.NewGuid()</c> justo antes de crear las credenciales). Es el lado contrario de la regla R1 de la
    /// spec 011: un operador de la plataforma nunca es miembro de una empresa. Se cumple porque ninguna ruta añade una
    /// pertenencia a una cuenta que ya existe; si alguna lo hiciera (una cuenta con dos empresas, un «invitar a quien ya
    /// tiene cuenta»), un operador podría acabar siendo miembro. Un sitio nuevo exige repensar R1 y añadirlo aquí a propósito.
    /// </summary>
    private static readonly string[] TenantMembershipCreators =
    [
        "src/OfiFlow.Application/Identity/Commands/Register/RegisterCommandHandler.cs",
        "src/OfiFlow.Application/Identity/Commands/AcceptInvitation/AcceptInvitationCommandHandler.cs"
    ];

    [Fact]
    public void TenantMemberships_AreOnlyCreatedForBrandNewAccounts()
    {
        var creators = SourceCode.FilesContaining("TenantUser.Create");

        var offenders = creators.Where(path => !TenantMembershipCreators.Contains(path)).ToList();
        Assert.True(offenders.Count == 0,
            "Solo el registro y aceptar una invitación pueden crear una pertenencia, y siempre para una cuenta recién creada " +
            $"(spec 011 R1: un operador nunca es miembro de una empresa). Aparece en: {string.Join(", ", offenders)}");

        // Control positivo: los dos sitios permitidos siguen creando pertenencias. Si uno dejara de hacerlo, la lista estaría
        // describiendo un código que ya no existe.
        Assert.All(TenantMembershipCreators, path => Assert.Contains(path, creators));
    }

    [Fact]
    public void TheTenantsAllowList_StillCorrespondsToARealRead()
    {
        // Control positivo de la lista blanca de «Tenants» (spec 011): si TenantStatus dejara de leer esa tabla (o se moviera),
        // el test de las lecturas globales pasaría en vacío para ella. Este avisa de que la lista ya no corresponde a nada.
        Assert.Contains("src/OfiFlow.Application/Tenancy/TenantStatus.cs", SourceCode.FilesContaining("dbContext.Tenants"));
    }

    [Fact]
    public void TheRawSqlScan_FindsTheLegitimateUse()
    {
        // Control positivo: si AccountLockout dejara de usar SQL (o el fichero se moviera), el test de arriba
        // pasaría en vacío. Este avisa de que la lista blanca ya no corresponde a nada.
        Assert.Contains(RawSqlAllowedFile, SourceCode.FilesContaining("SqlQuery<"));
    }

    [Fact]
    public void ThePasswordCheckWithoutLockout_IsNotUsedOutsideIdentityService()
    {
        // Comprobar una contraseña sin contar el intento es justo lo que el bloqueo prohíbe. Solo existe en el
        // propio IdentityService; para entrar se usa IIdentityService.AuthenticateAsync, que sí cuenta (R18).
        var offenders = SourceCode.FilesContaining("ValidateCredentialsWithoutLockoutAsync")
            .Where(path => path != "src/OfiFlow.Infrastructure/Identity/IdentityService.cs")
            .ToList();

        Assert.True(offenders.Count == 0,
            "ValidateCredentialsWithoutLockoutAsync no cuenta intentos ni bloquea; usa IIdentityService.AuthenticateAsync. " +
            $"Aparece en: {string.Join(", ", offenders)}");
        Assert.Contains("src/OfiFlow.Infrastructure/Identity/IdentityService.cs", SourceCode.FilesContaining("ValidateCredentialsWithoutLockoutAsync"));
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

    /// <summary>
    /// Los ÚNICOS sitios que emiten una sesión nueva (spec 010 R12): el login de una cuenta sin segundo factor y el canje
    /// del desafío, que exige el código. El refresh no emite sesiones nuevas: rota una que ya existe.
    /// </summary>
    private static readonly string[] SessionIssuers =
    [
        "src/OfiFlow.Application/Identity/Commands/Login/LoginCommandHandler.cs",
        "src/OfiFlow.Application/Identity/Commands/VerifyMfaChallenge/VerifyMfaChallengeCommandHandler.cs"
    ];

    [Fact]
    public void OnlyTheLoginAndTheChallengeRedemption_CanIssueANewSession()
    {
        // Es la promesa del segundo factor: una cuenta con MFA no recibe sesión sin un código válido. Si alguien añade
        // un camino nuevo que llame a IssueTokensAsync (un "login con Google", un "recordar dispositivo"...), este test
        // falla y obliga a decidir, a propósito, qué pasa con el segundo factor en ese camino.
        var definitions = new[]
        {
            "src/OfiFlow.Application/Common/Abstractions/ITokenService.cs",
            "src/OfiFlow.Infrastructure/Identity/TokenService.cs"
        };

        var callers = SourceCode.FilesContaining("IssueTokensAsync(").Where(path => !definitions.Contains(path)).Order().ToList();

        Assert.True(callers.SequenceEqual(SessionIssuers.Order()),
            "Solo el login y el canje del desafío pueden emitir una sesión (ADR-014, spec 010 R12). " +
            $"Hoy la emiten: {string.Join(", ", callers)}");
    }

    [Fact]
    public void TheLoginBranchThatNeedsASecondFactor_ReturnsBeforeIssuingAnySession()
    {
        // En el login, la rama "falta el segundo factor" debe terminar (return) ANTES de la llamada que emite la sesión.
        var login = SourceCode.Files("OfiFlow.Application/Identity/Commands/Login")
            .Single(file => file.Path.EndsWith("LoginCommandHandler.cs", StringComparison.Ordinal)).Content;

        var secondFactorBranch = login.IndexOf("if (authentication.SecondFactorRequired)", StringComparison.Ordinal);
        var issue = login.IndexOf("IssueTokensAsync(", StringComparison.Ordinal);

        Assert.True(secondFactorBranch >= 0, "No se encuentra la rama del segundo factor en el login.");
        Assert.True(issue > secondFactorBranch,
            "La emisión de la sesión debe ir DESPUÉS de la rama que exige el segundo factor, que termina con un return.");
        Assert.Contains("return new LoginResult.SecondFactorRequired", login[secondFactorBranch..issue]);
    }

    private const string PlatformLoginHandler = "src/OfiFlow.Application/Platform/Commands/PlatformLogin/PlatformLoginCommandHandler.cs";

    private const string PlatformRedemptionHandler = "src/OfiFlow.Application/Platform/Commands/VerifyPlatformChallenge/VerifyPlatformChallengeCommandHandler.cs";

    [Fact]
    public void OnlyTheChallengeRedemptionOfThePlatform_CanIssueAPlatformSession()
    {
        // Spec 011 R8: junto al login sin segundo factor y al canje del desafío de empresa, hay UN tercer sitio que emite una sesión,
        // el canje del desafío de plataforma. Si alguien añade otro camino (un «recordarme», una renovación), este test falla y
        // obliga a decidir, a propósito, qué pasa con el segundo factor en él.
        var definitions = new[]
        {
            "src/OfiFlow.Application/Common/Abstractions/IPlatformTokenService.cs",
            "src/OfiFlow.Infrastructure/Identity/PlatformTokenService.cs"
        };

        var callers = SourceCode.FilesContaining("IssuePlatformSession(").Where(path => !definitions.Contains(path)).Order().ToList();

        Assert.True(callers.SequenceEqual([PlatformRedemptionHandler]),
            $"Solo el canje del desafío de plataforma puede emitir una sesión de plataforma (spec 011 R8). Hoy la emiten: {string.Join(", ", callers)}");
    }

    [Fact]
    public void ThePlatformLogin_NeverIssuesAnySession_AndOnlyCreatesChallengesForThePlatform()
    {
        // R5: la contraseña de un operador nunca da una sesión, solo un desafío, y de plataforma (R9). La clase ni siquiera
        // recibe un servicio de tokens.
        var login = SourceCode.ReadText(PlatformLoginHandler);

        Assert.DoesNotContain("ITokenService", login);
        Assert.DoesNotContain("IPlatformTokenService", login);
        Assert.DoesNotContain("IssuePlatformSession", login);
        Assert.DoesNotContain("IssueTokensAsync", login);
        Assert.Contains("ChallengeAudience.Platform", login);
        Assert.DoesNotContain("ChallengeAudience.Tenant", login);
    }

    [Fact]
    public void ThePlatformRedemption_RedeemsThenChecksTheOperatorThenIssuesThenAudits()
    {
        // El orden es la seguridad: canjear el desafío de plataforma, volver a comprobar que sigue siendo un operador activo (R10),
        // emitir la sesión, y escribir y CONFIRMAR la auditoría antes de devolverla (R17).
        var handler = SourceCode.ReadText(PlatformRedemptionHandler);

        var redeem = handler.IndexOf("RedeemChallengeAsync(", StringComparison.Ordinal);
        var check = handler.IndexOf("IsActiveOperatorAsync(", StringComparison.Ordinal);
        var issue = handler.IndexOf("IssuePlatformSession(", StringComparison.Ordinal);
        var audit = handler.IndexOf("platformAudit.Add(", StringComparison.Ordinal);
        var save = handler.IndexOf("SaveChangesAsync(", StringComparison.Ordinal);

        Assert.True(redeem >= 0 && check > redeem && issue > check && audit > issue && save > audit,
            "El canje de plataforma debe ir en este orden: RedeemChallengeAsync, IsActiveOperatorAsync, IssuePlatformSession, platformAudit.Add, SaveChangesAsync.");
        Assert.Contains("ChallengeAudience.Platform", handler);
        Assert.DoesNotContain("ChallengeAudience.Tenant", handler);
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
            "Users, Tenants, ApplicationUsers, RefreshTokens y las tablas del segundo factor no tienen filtro de tenant (spec 007, ADR-009 R2): " +
            "leerlos puede exponer datos de todas las empresas. Solo se leen en los ficheros de GlobalDbSetReadAllowList; " +
            $"para añadir uno, justifícalo ahí. Accesos no permitidos: {string.Join("; ", offenders)}");
    }

    /// <summary>
    /// Las dos únicas operaciones que reciben un <c>TenantId</c> por la petición (spec 011): el panel de la plataforma suspende o
    /// reactiva la empresa que el operador elige. Ahí el Id es el OBJETIVO de la acción, no el contexto de quien la pide (un operador no
    /// tiene empresa). Es una lista cerrada y con nombre: cualquier otra operación sigue sin poder recibirlo.
    /// </summary>
    private static readonly string[] OperationsThatTargetATenant =
    [
        "OfiFlow.Application.Platform.Commands.SuspendTenant.SuspendTenantCommand",
        "OfiFlow.Application.Platform.Commands.ReactivateTenant.ReactivateTenantCommand"
    ];

    [Fact]
    public void NoCommandOrQuery_AcceptsTenantIdFromTheClient_ExceptTheTwoNamedPlatformOperations()
    {
        var withTenantId = typeof(DependencyInjection).Assembly.GetTypes()
            .Where(type => type.GetInterfaces().Any(IsMediatRRequest))
            .Where(type => type.GetProperty("TenantId") is not null)
            .ToList();

        var offenders = withTenantId
            .Select(type => type.FullName)
            .Where(name => !OperationsThatTargetATenant.Contains(name))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Un Command/Query no puede recibir TenantId: se toma siempre del JWT vía ITenantContext " +
            $"(ADR-002, ADR-009 R2, protección contra mass assignment). Lo tienen: {string.Join(", ", offenders)}");

        // Control positivo: las dos excepciones existen de verdad y son operaciones de plataforma. Si una dejara de tener TenantId, la
        // lista estaría describiendo un código que ya no existe; si una excepción se colara en una operación de empresa, no pasaría de aquí.
        Assert.Equal(OperationsThatTargetATenant.Order(), withTenantId.Select(type => type.FullName!).Order());
        Assert.All(withTenantId, type => Assert.True(type.IsDefined(typeof(RequiresPlatformOperatorAttribute), inherit: true),
            $"{type.Name}: solo una operación de plataforma puede recibir el Id de la empresa objetivo."));
    }

    private static bool IsMediatRRequest(Type type) =>
        type == typeof(IRequest) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IRequest<>));

}
