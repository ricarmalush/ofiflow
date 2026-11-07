using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Api.Tests.Architecture;

/// <summary>
/// Lo que protege la puerta del segundo factor obligatorio (spec 022, ADR-024) y que ningún test de comportamiento ve hasta que es tarde. Son escaneos del código (ADR-009): si alguien
/// ampliara la lista de operaciones permitidas «porque esta también hace falta», pusiera la comprobación antes del permiso, repitiera la regla en otro sitio o registrara otra
/// implementación de la regla, los tests de camino feliz seguirían en verde y la puerta quedaría abierta o rota.
/// </summary>
public class SecondFactorGateArchitectureTests(OfiFlowApiFactory factory) : IClassFixture<OfiFlowApiFactory>
{
    private const string Behavior = "src/OfiFlow.Application/Common/Behaviors/AuthorizationBehavior.cs";

    private static string[] CodeLines(string text) =>
        [.. text.Split('\n').Select(line => line.TrimStart()).Where(line => !line.StartsWith("//", StringComparison.Ordinal) && !line.StartsWith("///", StringComparison.Ordinal))];

    private static string Code(string path) => string.Join('\n', CodeLines(SourceCode.ReadText(path)));

    private static Type[] OperationsAllowedWithoutTheFactor() =>
        [.. typeof(OfiFlow.Application.DependencyInjection).Assembly.GetTypes().Where(type => type.IsDefined(typeof(AllowedWithoutSecondFactorAttribute), inherit: true))];

    // ===== La lista cerrada de operaciones permitidas (R3) =====

    [Fact]
    public void TheOperationsAllowedWithoutTheSecondFactor_AreExactlyTheFour()
    {
        // Control positivo y lista cerrada a la vez: si se añade una marca (o se quita una), este test falla y obliga a decidirlo a la vista, y a actualizar la spec 022.
        var names = OperationsAllowedWithoutTheFactor().Select(type => type.Name).Order().ToArray();

        Assert.Equal(["EnableMfaCommand", "GetCurrentAccountQuery", "GetMfaStatusQuery", "StartMfaSetupCommand"], names);
    }

    [Fact]
    public void EveryAllowedOperation_IsACompanyOperationWithAPermission_NeverAnonymousNorOfThePlatform()
    {
        // La marca solo tiene sentido en la puerta de las operaciones con permiso de empresa. En una anónima o de plataforma no hace nada y, si alguien la pusiera ahí, sugeriría
        // que esas pasan por la puerta (no pasan): se prohíbe para que no engañe.
        foreach (var operation in OperationsAllowedWithoutTheFactor())
        {
            Assert.True(operation.IsDefined(typeof(RequiresPermissionAttribute), inherit: true), $"{operation.Name} está permitida sin el factor pero no declara un permiso de empresa.");
            Assert.False(operation.IsDefined(typeof(AllowAnonymousRequestAttribute), inherit: true), $"{operation.Name} es anónima: la marca no tiene sentido.");
            Assert.False(operation.IsDefined(typeof(RequiresPlatformOperatorAttribute), inherit: true), $"{operation.Name} es de plataforma: la marca no tiene sentido.");
        }
    }

    [Fact]
    public void EveryAllowedOperation_BelongsToWhoCanActivateTheFactorOrKnowWhoTheyAre()
    {
        // Lo que se permite sin el factor es solo: ver quién soy (Account.Read) y activarlo (Mfa.Manage). Ningún otro permiso (clientes, trabajos, equipo…) puede colarse en la lista.
        var allowedPermissions = new[] { Permission.AccountRead, Permission.MfaManage };

        foreach (var operation in OperationsAllowedWithoutTheFactor())
        {
            var permissions = operation.GetCustomAttributes(typeof(RequiresPermissionAttribute), inherit: true).Cast<RequiresPermissionAttribute>().Select(a => a.Permission);

            Assert.All(permissions, permission => Assert.Contains(permission, allowedPermissions));
        }
    }

    [Fact]
    public void NoOperationThatChangesOrReadsBusinessData_IsAllowedWithoutTheFactor()
    {
        // La contraprueba de la lista: ninguna operación de clientes, trabajos, equipo o sesiones lleva la marca.
        var allowed = OperationsAllowedWithoutTheFactor().Select(type => type.Namespace ?? string.Empty);

        Assert.DoesNotContain(allowed, ns => ns.Contains(".Customers", StringComparison.Ordinal) || ns.Contains(".Jobs", StringComparison.Ordinal) || ns.Contains(".Tenancy", StringComparison.Ordinal));
    }

    // ===== Dónde vive la puerta =====

    [Fact]
    public void TheGate_IsCheckedAfterThePermission_NeverBefore()
    {
        // ADR-012 R3: quien no tiene el permiso recibe «auth.forbidden» como siempre, tenga o no el factor. Si la puerta fuera antes, un rol sin permiso oiría «activa el factor».
        var code = Code(Behavior);

        var permission = code.IndexOf("RolePermissions.Has(", StringComparison.Ordinal);
        var gate = code.IndexOf("SecondFactorEnrollmentRequired", StringComparison.Ordinal);
        var verify = code.IndexOf("VerifyMembershipAsync(", StringComparison.Ordinal);

        Assert.True(verify >= 0 && permission > verify, "El permiso se comprueba después de verificar la pertenencia.");
        Assert.True(gate > permission, "La puerta del segundo factor va DESPUÉS de la comprobación del permiso.");
    }

    [Fact]
    public void TheEnrollmentRequiredDenial_IsThrownOnlyByTheAuthorizationBehavior()
    {
        // Un solo sitio deniega con este código: si otro handler lo lanzara, la puerta tendría dos dueños y podrían contradecirse.
        var throwers = SourceCode.FilesContaining("MfaErrors.EnrollmentRequired", "OfiFlow.Application").Concat(SourceCode.FilesContaining("MfaErrors.EnrollmentRequired", "OfiFlow.Infrastructure"))
            .Concat(SourceCode.FilesContaining("MfaErrors.EnrollmentRequired", "OfiFlow.Api")).Order().ToList();

        Assert.Equal([Behavior], throwers);
    }

    [Fact]
    public void TheDecisionOfWhoMustHaveTheFactor_LivesInOnePlaceOnly()
    {
        // La regla por rol (Owner y Admin) está solo en la política de Domain. Ningún otro fichero de src la consulta salvo la implementación de la interfaz que la envuelve.
        var users = SourceCode.FilesContaining("SecondFactorPolicy.RequiredFor(", "OfiFlow.Application")
            .Concat(SourceCode.FilesContaining("SecondFactorPolicy.RequiredFor(", "OfiFlow.Infrastructure"))
            .Concat(SourceCode.FilesContaining("SecondFactorPolicy.RequiredFor(", "OfiFlow.Api"))
            .Order().ToList();

        Assert.Equal(["src/OfiFlow.Infrastructure/Identity/SecondFactorRequirement.cs"], users);
        Assert.Contains("TenantRole.Owner or TenantRole.Admin", Code("src/OfiFlow.Domain/Identity/SecondFactorPolicy.cs"));
    }

    [Fact]
    public void TheRealRequirement_IsTheOnlyOneRegisteredInSrc_AndItDelegatesToThePolicy()
    {
        // Producción siempre tiene la regla real. Solo los tests sustituyen la interfaz; ningún código de src puede registrar otra implementación.
        string[] registrationVerbs = ["AddSingleton", "AddScoped", "AddTransient", "TryAdd", "Replace(", "Configure", "Decorate"];
        var registrations = SourceCode.Files()
            .Where(file => CodeLines(file.Content).Any(line => line.Contains("ISecondFactorRequirement", StringComparison.Ordinal) && registrationVerbs.Any(verb => line.Contains(verb, StringComparison.Ordinal))))
            .Select(file => file.Path)
            .Order().ToList();

        Assert.Equal(["src/OfiFlow.Infrastructure/DependencyInjection.cs"], registrations);
        Assert.Contains("AddSingleton<ISecondFactorRequirement, SecondFactorRequirement>()", Code("src/OfiFlow.Infrastructure/DependencyInjection.cs"));

        var implementation = Code("src/OfiFlow.Infrastructure/Identity/SecondFactorRequirement.cs");
        Assert.Contains("SecondFactorPolicy.RequiredFor(role)", implementation);
        Assert.DoesNotContain("=> false", implementation);
        Assert.DoesNotContain("Environment", implementation);
        Assert.DoesNotContain("Configuration", implementation);
    }

    [Fact]
    public void NoSourceFile_ReadsAConfigurationToTurnTheObligationOff()
    {
        // No existe un interruptor por configuración: un valor olvidado en producción no puede apagar la obligación. Solo cambia con código.
        var offenders = SourceCode.Files("OfiFlow.Infrastructure").Concat(SourceCode.Files("OfiFlow.Application")).Concat(SourceCode.Files("OfiFlow.Api"))
            .Where(file => file.Content.Contains("SecondFactor", StringComparison.Ordinal) && file.Content.Contains("GetValue<", StringComparison.Ordinal))
            .Select(file => file.Path).ToList();

        Assert.Empty(offenders);
    }

    // ===== El cableado de verdad =====

    [Fact]
    public void TheRealHost_RegistersTheRealRequirement_AndItRequiresTheFactorFromOwnerAndAdmin()
    {
        // Si alguien borrara el registro, CurrentUser no se podría construir (y la API no sabría a quién obligar). Este test arranca la API real, sin ninguna sustitución de los tests.
        var requirement = factory.Services.GetRequiredService<ISecondFactorRequirement>();

        Assert.Equal("SecondFactorRequirement", requirement.GetType().Name);
        Assert.True(requirement.IsRequiredFor(TenantRole.Owner));
        Assert.True(requirement.IsRequiredFor(TenantRole.Admin));
        Assert.False(requirement.IsRequiredFor(TenantRole.Manager));
        Assert.False(requirement.IsRequiredFor(TenantRole.Technician));
        Assert.False(requirement.IsRequiredFor(TenantRole.Employee));
    }
}
