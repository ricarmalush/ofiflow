using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Security;

/// <summary>La misma API que <see cref="MfaFlowFixture"/>, pero con la regla REAL de producción: Owner y Admin sin segundo factor activo no pueden usar la aplicación (spec 022).</summary>
public sealed class SecondFactorGateFixture : MfaFlowFixture
{
    protected override bool EnforceSecondFactor => true;
}

/// <summary>
/// La puerta del segundo factor obligatorio de principio a fin por HTTP (spec 022, bloque 1): quien debe activarlo y no lo ha hecho solo puede ver quién es y activarlo; en cuanto lo activa,
/// todo vuelve a funcionar. Cada denegación va con su control positivo (la misma petición con el resultado bueno) para que un «falla» no se deba a un error de la preparación.
/// </summary>
public class SecondFactorGateEndToEndTests(SecondFactorGateFixture fixture) : IClassFixture<SecondFactorGateFixture>
{
    private const string EnrollmentRequired = "mfa.enrollment_required";

    /// <summary>Operaciones de negocio y de cuenta que la puerta debe cerrar (todas con permiso de empresa que Owner y Admin tienen, y ninguna en la lista de permitidas).</summary>
    public static TheoryData<string, string> ClosedOperations => new()
    {
        { "GET", "/api/v1/customers" },
        { "GET", "/api/v1/jobs" },
        { "GET", "/api/v1/jobs/agenda" },
        // Spec 023: los presupuestos son operaciones con permiso de empresa y ninguna está en la lista de permitidas.
        { "GET", "/api/v1/quotes" },
        { "POST", "/api/v1/quotes/00000000-0000-0000-0000-000000000001/send" },
        // Spec 024: el perfil fiscal tiene permisos de empresa y ninguna operación suya está en la lista de permitidas. (El PUT necesita cuerpo: tiene sus propios tests más abajo.)
        { "GET", "/api/v1/company/profile" },
        { "GET", "/api/v1/users" },
        { "GET", "/api/v1/account/sessions" },
        { "DELETE", "/api/v1/account/sessions" },
        { "POST", "/api/v1/account/mfa/recovery-codes" },
        { "POST", "/api/v1/account/mfa/disable" }
    };

    private static HttpMethod Method(string name) => new(name);

    /// <summary>Un Owner con el segundo factor (para poder invitar) y a partir de él, un Admin recién llegado que todavía NO lo tiene.</summary>
    private async Task<(MfaFlowFixture.Account Owner, MfaFlowFixture.Account Admin)> OwnerWithFactorAndAdminWithoutAsync()
    {
        var owner = await fixture.NewMfaAccountAsync();
        var admin = await fixture.InviteAndJoinAsync(owner, TenantRole.Admin);

        return (owner, admin);
    }

    private async Task AssertEnrollmentRequiredAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await MfaFlowFixture.JsonOf(response);
        Assert.Equal(EnrollmentRequired, problem.GetProperty("code").GetString());
        Assert.Equal("Tu rol exige el segundo factor. Actívalo para continuar.", problem.GetProperty("detail").GetString());
    }

    private async Task<string> MeAsync(MfaFlowFixture.Account account)
    {
        var response = await fixture.SendAsync(HttpMethod.Get, "/api/v1/account/me", bearer: account.AccessToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync();
    }

    // ===== Lo que se cierra =====

    [Theory]
    [MemberData(nameof(ClosedOperations))]
    public async Task AnOwnerWithoutTheFactor_IsDeniedEverythingThatIsNotMarkedAsAllowed(string method, string path)
    {
        var owner = await fixture.RegisterOwnerAsync();

        var response = await fixture.SendAsync(Method(method), path, method == "POST" ? new { password = owner.Password, code = "000000" } : null, owner.AccessToken);

        await AssertEnrollmentRequiredAsync(response);
    }

    [Theory]
    [MemberData(nameof(ClosedOperations))]
    public async Task AnAdminWithoutTheFactor_IsDeniedEverythingThatIsNotMarkedAsAllowed(string method, string path)
    {
        var (_, admin) = await OwnerWithFactorAndAdminWithoutAsync();

        var response = await fixture.SendAsync(Method(method), path, method == "POST" ? new { password = admin.Password, code = "000000" } : null, admin.AccessToken);

        await AssertEnrollmentRequiredAsync(response);
    }

    private static object ValidProfile() =>
        new { legalName = "Marta Gil Pérez", taxId = "12345678Z", addressLine = "Calle Mayor 1", postalCode = "28001", city = "Madrid" };

    [Fact]
    public async Task AnOwnerWithoutTheFactor_CannotSaveTheCompanyProfile_AndNothingIsStored()
    {
        var owner = await fixture.RegisterOwnerAsync();

        var response = await fixture.SendAsync(HttpMethod.Put, "/api/v1/company/profile", ValidProfile(), owner.AccessToken);

        await AssertEnrollmentRequiredAsync(response);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(0, await db.CompanyProfiles.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task AnAdminWithoutTheFactor_CannotSaveTheCompanyProfile()
    {
        var (_, admin) = await OwnerWithFactorAndAdminWithoutAsync();

        var response = await fixture.SendAsync(HttpMethod.Put, "/api/v1/company/profile", ValidProfile(), admin.AccessToken);

        await AssertEnrollmentRequiredAsync(response);
    }

    [Fact]
    public async Task ThePositiveControl_TheSameOperationsWork_ForAnOwnerWhoHasTheFactor()
    {
        // Sin este control, un «403» podría ser un fallo de la preparación (ruta mal escrita, falta de permiso) y no la puerta.
        var owner = await fixture.NewMfaAccountAsync();

        foreach (var path in new[] { "/api/v1/customers", "/api/v1/jobs", "/api/v1/jobs/agenda?from=2026-11-01T00:00:00Z&to=2026-11-08T00:00:00Z", "/api/v1/users", "/api/v1/account/sessions", "/api/v1/company/profile" })
        {
            Assert.Equal(HttpStatusCode.OK, (await fixture.SendAsync(HttpMethod.Get, path, bearer: owner.AccessToken)).StatusCode);
        }
    }

    [Fact]
    public async Task AWriteOperation_IsDeniedToo_AndNothingIsCreated()
    {
        var owner = await fixture.RegisterOwnerAsync();

        var create = await fixture.SendAsync(HttpMethod.Post, "/api/v1/customers", new { type = 0, name = "Cliente de prueba", email = "cliente@example.com", phone = "600000000", address = "Calle 1", notes = (string?)null }, owner.AccessToken);

        await AssertEnrollmentRequiredAsync(create);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(0, await db.Customers.IgnoreQueryFilters().CountAsync(c => c.Name == "Cliente de prueba"));
    }

    // ===== Lo que se permite =====

    [Fact]
    public async Task WhoMustEnroll_CanSeeWhoTheyAre_AndTheStatusOfTheFactor()
    {
        var owner = await fixture.RegisterOwnerAsync();

        Assert.Contains("Owner", await MeAsync(owner));
        var status = await fixture.SendAsync(HttpMethod.Get, "/api/v1/account/mfa", bearer: owner.AccessToken);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.False((await MfaFlowFixture.JsonOf(status)).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task WhoMustEnroll_CanStartTheSetup_AndConfirmIt_AndThenEverythingWorks()
    {
        var owner = await fixture.RegisterOwnerAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: owner.AccessToken)).StatusCode);

        // El camino de la obligación: pedir el secreto con la contraseña y confirmarlo con el primer código de la app.
        Assert.Equal(HttpStatusCode.OK, (await fixture.SetupAsync(owner)).StatusCode);
        var enable = await fixture.SendAsync(HttpMethod.Post, "/api/v1/account/mfa/enable", new { code = fixture.CodeNow(owner) }, owner.AccessToken);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        // Activarlo cierra las sesiones (spec 010): se entra de nuevo con el segundo factor y ya no hay puerta.
        await fixture.LoginWithMfaAsync(owner);
        foreach (var path in new[] { "/api/v1/customers", "/api/v1/jobs", "/api/v1/users", "/api/v1/account/sessions", "/api/v1/company/profile" })
        {
            Assert.Equal(HttpStatusCode.OK, (await fixture.SendAsync(HttpMethod.Get, path, bearer: owner.AccessToken)).StatusCode);
        }
    }

    [Fact]
    public async Task ASecretThatWasRequestedButNotConfirmed_DoesNotLiftTheObligation()
    {
        var owner = await fixture.RegisterOwnerAsync();

        Assert.Equal(HttpStatusCode.OK, (await fixture.SetupAsync(owner)).StatusCode);

        await AssertEnrollmentRequiredAsync(await fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: owner.AccessToken));
        await AssertEnrollmentRequiredAsync(await fixture.SendAsync(HttpMethod.Get, "/api/v1/users", bearer: owner.AccessToken));
    }

    [Fact]
    public async Task AWrongCodeWhileEnrolling_KeepsTheObligation_AndTheOrdinaryErrorIsReturned()
    {
        // La operación de activar sí se ejecuta (está permitida): falla por su propia razón (código inválido), no por la puerta.
        var owner = await fixture.RegisterOwnerAsync();
        Assert.Equal(HttpStatusCode.OK, (await fixture.SetupAsync(owner)).StatusCode);
        var right = fixture.CodeNow(owner);

        var wrong = await fixture.SendAsync(HttpMethod.Post, "/api/v1/account/mfa/enable", new { code = right == "000000" ? "000001" : "000000" }, owner.AccessToken);

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("mfa.invalid_code", (await MfaFlowFixture.JsonOf(wrong)).GetProperty("code").GetString());
        await AssertEnrollmentRequiredAsync(await fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: owner.AccessToken));
    }

    // ===== Quién no queda afectado =====

    [Theory]
    [InlineData(TenantRole.Manager)]
    [InlineData(TenantRole.Technician)]
    [InlineData(TenantRole.Employee)]
    public async Task TheRolesThatDoNotRequireTheFactor_ContinueAsBefore_WithoutIt(TenantRole role)
    {
        var owner = await fixture.NewMfaAccountAsync();
        var member = await fixture.InviteAndJoinAsync(owner, role);

        // Todos tienen Account.Read; Manager, además, lee clientes. La puerta no interviene aunque no tengan el factor.
        Assert.Equal(HttpStatusCode.OK, (await fixture.SendAsync(HttpMethod.Get, "/api/v1/account/sessions", bearer: member.AccessToken)).StatusCode);
        if (role == TenantRole.Manager)
        {
            Assert.Equal(HttpStatusCode.OK, (await fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: member.AccessToken)).StatusCode);
        }
    }

    [Fact]
    public async Task WhoLacksThePermission_GetsTheOrdinaryForbidden_NotTheEnrollmentOne()
    {
        // Un Technician no puede listar el equipo (Users.Read se lo niega): recibe «auth.forbidden» de siempre, no «activa el factor».
        var owner = await fixture.NewMfaAccountAsync();
        var technician = await fixture.InviteAndJoinAsync(owner, TenantRole.Technician);

        var response = await fixture.SendAsync(HttpMethod.Get, "/api/v1/users", bearer: technician.AccessToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.forbidden", (await MfaFlowFixture.JsonOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnAnonymousOperation_IsNotAffected()
    {
        var owner = await fixture.RegisterOwnerAsync();

        // El login de una cuenta que debe activar el factor sigue funcionando (de hecho es como llega a la pantalla de activarlo).
        var login = await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = owner.Email, password = owner.Password });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    // ===== Efecto inmediato (D4), con el mismo token =====

    [Fact]
    public async Task APromotionToAdmin_TakesEffectOnTheNextRequest_WithTheSameToken()
    {
        var owner = await fixture.NewMfaAccountAsync();
        var manager = await fixture.InviteAndJoinAsync(owner, TenantRole.Manager);
        Assert.Equal(HttpStatusCode.OK, (await fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: manager.AccessToken)).StatusCode);

        var tenantUserId = await fixture.TenantUserIdOfAsync(owner, manager);
        var promote = await fixture.SendAsync(HttpMethod.Put, $"/api/v1/users/{tenantUserId}/role", new { role = (int)TenantRole.Admin }, owner.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, promote.StatusCode);

        // El token del Manager sigue diciendo «Manager» y aun así, desde la siguiente petición, se le exige el factor.
        await AssertEnrollmentRequiredAsync(await fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: manager.AccessToken));
    }

    [Fact]
    public async Task TheDenial_IsLoggedWithTheDedicatedEvent_AndWithoutAnySecret()
    {
        var owner = await fixture.RegisterOwnerAsync();
        var before = fixture.Logs.Entries.Count;

        await fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: owner.AccessToken);

        var entry = Assert.Single(fixture.Logs.Entries.Skip(before), e => e.EventId == SecurityEventIds.MfaEnrollmentRequiredDenied);
        Assert.Contains("GetCustomersQuery", entry.Message);
        Assert.Contains("Owner", entry.Message);
        Assert.DoesNotContain(owner.Email, entry.Message);
        Assert.DoesNotContain(owner.Password, entry.Message);
        Assert.DoesNotContain(owner.AccessToken, entry.Message);
    }

    [Fact]
    public async Task TheDenialOfACommandThatCarriesASecret_NeverLogsTheRequestBody()
    {
        // ADR-009 R5: el comando denegado lleva la contraseña en el cuerpo. El registro dice qué operación y quién, nunca lo que mandó (si se volcara el comando entero, saldría aquí).
        var owner = await fixture.RegisterOwnerAsync();
        var before = fixture.Logs.Entries.Count;

        var response = await fixture.SendAsync(HttpMethod.Post, "/api/v1/account/mfa/recovery-codes", new { password = owner.Password, code = "123456" }, owner.AccessToken);

        await AssertEnrollmentRequiredAsync(response);
        var written = fixture.Logs.Entries.Skip(before).ToList();
        Assert.Contains(written, e => e.EventId == SecurityEventIds.MfaEnrollmentRequiredDenied);
        Assert.DoesNotContain(written, e => e.Message.Contains(owner.Password, StringComparison.Ordinal));
        Assert.DoesNotContain(written, e => e.Message.Contains("123456", StringComparison.Ordinal));
    }
}
