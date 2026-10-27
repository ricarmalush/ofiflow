using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Platform;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// El recorrido completo del panel del operador y cada ataque descrito en la spec 011 (bloque 7), con la API entera, SQL Server real
/// y el reloj controlado del segundo factor. Los bloques 5 y 6 probaron cada pieza; aquí se prueba que, juntas, resisten a quien las
/// ataca: contra el bloqueo, contra los mundos mezclados, contra el mass assignment y contra el efecto de una empresa en otra.
/// </summary>
public sealed class PlatformPanelAttacksEndToEndTests(PlatformFlowFixture fixture) : IClassFixture<PlatformFlowFixture>
{
    private const string Tenants = "/api/v1/platform/tenants";

    private readonly PlatformFlowFixture _fixture = fixture;

    private sealed record Company(MfaFlowFixture.Account Owner, Guid TenantId, string Name);

    private async Task<Company> NewCompanyAsync()
    {
        var name = $"Empresa {Guid.NewGuid():N}";
        var owner = await _fixture.RegisterOwnerAsync(name);
        var tenantId = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(owner.AccessToken).Claims.Single(c => c.Type == "tenant_id").Value);

        return new Company(owner, tenantId, name);
    }

    private Task<HttpResponseMessage> Suspend(string token, Guid tenantId, object? body = null, string query = "") =>
        _fixture.SendAsync(HttpMethod.Post, $"{Tenants}/{tenantId}/suspend{query}", body, token);

    private Task<HttpResponseMessage> Reactivate(string token, Guid tenantId) =>
        _fixture.SendAsync(HttpMethod.Post, $"{Tenants}/{tenantId}/reactivate", bearer: token);

    private Task<HttpResponseMessage> CustomersOf(Company company) =>
        _fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: company.Owner.AccessToken);

    private static async Task<string> CodeOfAsync(HttpResponseMessage response) =>
        (await MfaFlowFixture.JsonOf(response)).GetProperty("code").GetString()!;

    private async Task<bool> IsSuspendedInTheListAsync(string token, Guid tenantId)
    {
        var page = await MfaFlowFixture.JsonOf(await _fixture.SendAsync(HttpMethod.Get, Tenants + "?pageSize=100", bearer: token));
        var item = page.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == tenantId);

        return item.GetProperty("suspendedAt").ValueKind != JsonValueKind.Null;
    }

    private async Task<List<string>> AuditActionsOfAsync(Guid operatorUserId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Set<PlatformAuditEntry>().AsNoTracking().Where(e => e.OperatorUserId == operatorUserId).Select(e => e.Action).ToListAsync();
    }

    // ===== El recorrido completo =====

    [Fact]
    public async Task TheWholeCycle_OperatorLogsIn_ListsSuspends_TheMemberIsCutOff_AndReactivationGivesItBack()
    {
        var company = await NewCompanyAsync();
        var other = await NewCompanyAsync();

        // 1. El operador entra en dos pasos con un código TOTP de verdad, calculado con el reloj controlado.
        var platformOperator = await _fixture.CreateOperatorAsync();
        var challenge = await _fixture.PlatformChallengeAsync(platformOperator);
        var verify = await _fixture.PlatformVerifyAsync(challenge, _fixture.FreshCode(platformOperator));
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var token = (await MfaFlowFixture.JsonOf(verify)).GetProperty("accessToken").GetString()!;

        // 2. Ve las dos empresas, activas.
        Assert.False(await IsSuspendedInTheListAsync(token, company.TenantId));
        Assert.False(await IsSuspendedInTheListAsync(token, other.TenantId));
        Assert.Equal(HttpStatusCode.OK, (await CustomersOf(company)).StatusCode);

        // 3. Suspende una: su miembro se queda fuera en la siguiente petición, y no puede abrir sesión nueva.
        Assert.Equal(HttpStatusCode.NoContent, (await Suspend(token, company.TenantId)).StatusCode);
        Assert.True(await IsSuspendedInTheListAsync(token, company.TenantId));
        var denied = await CustomersOf(company);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("tenant.suspended", await CodeOfAsync(denied));
        var login = await _fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = company.Owner.Email, password = company.Owner.Password });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);

        // 4. La otra empresa no se entera.
        Assert.Equal(HttpStatusCode.OK, (await CustomersOf(other)).StatusCode);
        Assert.False(await IsSuspendedInTheListAsync(token, other.TenantId));

        // 5. Reactiva: el mismo miembro, con la misma sesión, vuelve a operar.
        Assert.Equal(HttpStatusCode.NoContent, (await Reactivate(token, company.TenantId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CustomersOf(company)).StatusCode);
        Assert.False(await IsSuspendedInTheListAsync(token, company.TenantId));

        // 6. Todo quedó en la auditoría del operador: su sesión, la suspensión y la reactivación (y el alta de la cuenta).
        Assert.Equal(
            new[] { PlatformAuditActions.OperatorCreated, PlatformAuditActions.SessionIssued, PlatformAuditActions.TenantReactivated, PlatformAuditActions.TenantSuspended }.Order(),
            (await AuditActionsOfAsync(platformOperator.UserId)).Order());

        // 7. Y el analista tiene los eventos en el registro: la sesión (1601) antes de la suspensión (1604).
        var entries = _fixture.Logs.Entries.ToList();
        var sessionIssued = entries.FindIndex(e => e.EventId == SecurityEventIds.PlatformSessionIssued && e.Message.Contains(platformOperator.UserId.ToString(), StringComparison.Ordinal));
        var suspended = entries.FindIndex(e => e.EventId == SecurityEventIds.PlatformTenantSuspended && e.Message.Contains(company.TenantId.ToString(), StringComparison.Ordinal));
        Assert.True(sessionIssued >= 0 && suspended > sessionIssued, "Debe haber un 1601 del operador antes de su 1604.");
    }

    // ===== Contra el bloqueo =====

    [Fact]
    public async Task FiftySimultaneousWrongPlatformLogins_PerformExactlyFiveRealPasswordChecks_AndLockTheOperator()
    {
        var platformOperator = await _fixture.CreateOperatorAsync();
        var realBefore = _fixture.Checker.RealVerifications;

        // Cada petición lleva su IP (el límite por IP es otra defensa): lo que se prueba es el bloqueo POR CUENTA.
        var responses = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ =>
            Task.Run(() => _fixture.PlatformLoginAsync(platformOperator.Email, "incorrecta-" + Guid.NewGuid().ToString("N")))));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        Assert.Equal(5, _fixture.Checker.RealVerifications - realBefore);

        // Bloqueado: ni siquiera la contraseña correcta entra, y la respuesta es la misma 401 de siempre.
        var correct = await _fixture.PlatformLoginAsync(platformOperator.Email, platformOperator.Password);
        var wrong = await _fixture.PlatformLoginAsync(platformOperator.Email, "incorrecta");
        Assert.Equal(HttpStatusCode.Unauthorized, correct.StatusCode);
        Assert.Equal(await MfaFlowFixture.FingerprintAsync(wrong), await MfaFlowFixture.FingerprintAsync(correct));

        Assert.Contains(_fixture.Logs.Entries, e => e.EventId == SecurityEventIds.AccountLockedOut && e.Message.Contains(platformOperator.UserId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task FiveWrongCodes_KillTheChallenge_AndTheCorrectCodeThenDoesNotWork()
    {
        var platformOperator = await _fixture.CreateOperatorAsync();
        var challenge = await _fixture.PlatformChallengeAsync(platformOperator);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.PlatformVerifyAsync(challenge, "000000")).StatusCode);
        }

        // El sexto intento, con el código CORRECTO, tampoco sirve: el desafío está agotado (y la cuenta bloqueada).
        var late = await _fixture.PlatformVerifyAsync(challenge, _fixture.FreshCode(platformOperator));
        Assert.Equal(HttpStatusCode.Unauthorized, late.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.PlatformLoginAsync(platformOperator.Email, platformOperator.Password)).StatusCode);
    }

    // ===== Contra los mundos mezclados =====

    [Fact]
    public async Task AnOwner_CannotReachAnyPlatformRoute_NorTheirOwnCompanyThroughIt()
    {
        var company = await NewCompanyAsync();

        var list = await _fixture.SendAsync(HttpMethod.Get, Tenants, bearer: company.Owner.AccessToken);
        var suspend = await Suspend(company.Owner.AccessToken, company.TenantId);

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, suspend.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CustomersOf(company)).StatusCode); // y no pasó nada
    }

    [Fact]
    public async Task TheOperator_SeesNoCustomerNorJobNorUserOfAnyone_OnAnyCompanyRoute()
    {
        var company = await NewCompanyAsync();
        var token = await _fixture.PlatformTokenAsync(await _fixture.CreateOperatorAsync());

        foreach (var path in new[] { "/api/v1/customers", "/api/v1/jobs", "/api/v1/users", "/api/v1/invitations", "/api/v1/account/mfa" })
        {
            var response = await _fixture.SendAsync(HttpMethod.Get, path, bearer: token);

            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"GET {path} respondió {(int)response.StatusCode} a una sesión de plataforma.");
        }

        Assert.NotNull(company);
    }

    [Fact]
    public async Task ThePlatformSession_HasNoRefresh_AndTheTokenIsNoRefreshToken()
    {
        var platformOperator = await _fixture.CreateOperatorAsync();
        var token = await _fixture.PlatformTokenAsync(platformOperator);

        // No existe una ruta para renovar una sesión de plataforma...
        Assert.Equal(HttpStatusCode.NotFound, (await _fixture.SendAsync(HttpMethod.Post, "/api/v1/platform/auth/refresh", new { refreshToken = token })).StatusCode);
        // ...y presentar el token de acceso a la renovación de empresa no abre nada (un JWT no tiene la forma de un refresh token: lo para el
        // validador con un 400; si la tuviera, sería el 401 de un token desconocido).
        var refresh = await _fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = token });
        Assert.Contains(refresh.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized });
        Assert.DoesNotContain("accessToken", await refresh.Content.ReadAsStringAsync());
    }

    // ===== Contra el mass assignment =====

    [Fact]
    public async Task ATenantIdInTheBodyOrTheQuery_IsIgnored_OnlyTheRouteTargetIsSuspended()
    {
        var target = await NewCompanyAsync();
        var bystander = await NewCompanyAsync();
        var token = await _fixture.PlatformTokenAsync(await _fixture.CreateOperatorAsync());

        var response = await Suspend(token, target.TenantId, new { tenantId = bystander.TenantId }, query: $"?tenantId={bystander.TenantId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(await IsSuspendedInTheListAsync(token, target.TenantId));
        Assert.False(await IsSuspendedInTheListAsync(token, bystander.TenantId));
        Assert.Equal(HttpStatusCode.OK, (await CustomersOf(bystander)).StatusCode);
    }

    // ===== Un operador no es una cuenta de empresa (R1, R3) =====

    [Fact]
    public async Task TheEmailOfAnOperator_CannotBeRegisteredAsACompany_NorInvitedToOne()
    {
        var platformOperator = await _fixture.CreateOperatorAsync();
        var owner = await _fixture.RegisterOwnerAsync();

        var register = await _fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/register",
            new { companyName = "Empresa del operador", userName = "Operador", email = platformOperator.Email, password = MembersFixture.NewPassword() });
        var invite = await _fixture.SendAsync(HttpMethod.Post, "/api/v1/invitations", new { email = platformOperator.Email, role = (int)TenantRole.Technician }, owner.AccessToken);

        Assert.Equal(HttpStatusCode.BadRequest, register.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invite.StatusCode);
    }

    // ===== Dos empresas: lo que una suspensión no toca =====

    [Fact]
    public async Task SuspendingA_LeavesBWorkingInEverything_IncludingInvitationsRefreshAndNewRegistrations()
    {
        var a = await NewCompanyAsync();
        var b = await NewCompanyAsync();
        var token = await _fixture.PlatformTokenAsync(await _fixture.CreateOperatorAsync());

        // A tiene una invitación pendiente de antes de la suspensión.
        var pending = await _fixture.SendAsync(HttpMethod.Post, "/api/v1/invitations", new { email = $"pendiente-{Guid.NewGuid():N}@example.com", role = (int)TenantRole.Technician }, a.Owner.AccessToken);
        Assert.Equal(HttpStatusCode.Created, pending.StatusCode);
        var pendingToken = (await MfaFlowFixture.JsonOf(pending)).GetProperty("token").GetString();

        Assert.Equal(HttpStatusCode.NoContent, (await Suspend(token, a.TenantId)).StatusCode);

        // A: nadie acepta su invitación, y su miembro no renueva la sesión.
        var accept = await _fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/accept-invitation", new { token = pendingToken, name = "Persona", password = MembersFixture.NewPassword() });
        Assert.Equal(HttpStatusCode.BadRequest, accept.StatusCode);
        Assert.Equal("invitation.invalid", await CodeOfAsync(accept));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = a.Owner.RefreshToken })).StatusCode);

        // B: su equipo sigue trabajando, invita, renueva, y una empresa nueva puede registrarse (R16).
        var member = await _fixture.InviteAndJoinAsync(b.Owner, TenantRole.Technician);
        Assert.Equal(HttpStatusCode.OK, (await _fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: member.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = b.Owner.RefreshToken })).StatusCode);
        var newcomer = await _fixture.RegisterOwnerAsync();
        Assert.Equal(HttpStatusCode.OK, (await _fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: newcomer.AccessToken)).StatusCode);

        // Reactivada A, su refresh token (que no se borró) vuelve a valer.
        Assert.Equal(HttpStatusCode.NoContent, (await Reactivate(token, a.TenantId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = a.Owner.RefreshToken })).StatusCode);
    }

    // ===== El operador dado de baja =====

    [Fact]
    public async Task ADisabledOperator_LosesEverythingAtOnce_Panel_NewSessions_AndTheChallengeInFlight()
    {
        var company = await NewCompanyAsync();
        var platformOperator = await _fixture.CreateOperatorAsync();
        var token = await _fixture.PlatformTokenAsync(platformOperator);
        var challenge = await _fixture.PlatformChallengeAsync(platformOperator);

        await _fixture.DisableOperatorAsync(platformOperator);

        Assert.Equal(HttpStatusCode.Forbidden, (await _fixture.SendAsync(HttpMethod.Get, Tenants, bearer: token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Suspend(token, company.TenantId)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.PlatformVerifyAsync(challenge, _fixture.FreshCode(platformOperator))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.PlatformLoginAsync(platformOperator.Email, platformOperator.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CustomersOf(company)).StatusCode); // y la empresa no se tocó

        // El analista ve la baja haciendo efecto: un 1607 en el alta de la baja (programa del servidor) y un 1608 en la petición cortada.
        Assert.Contains(_fixture.Logs.Entries, e => e.EventId == SecurityEventIds.PlatformSessionRevoked && e.Message.Contains(platformOperator.UserId.ToString(), StringComparison.Ordinal));
        Assert.Contains(_fixture.Logs.Entries, e => e.EventId == SecurityEventIds.PlatformOperatorDisabled && e.Message.Contains(platformOperator.UserId.ToString(), StringComparison.Ordinal));
    }
}
