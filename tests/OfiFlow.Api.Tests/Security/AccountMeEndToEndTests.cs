using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text.Json;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// «Quién soy» (spec 012) de punta a punta, con la API entera y SQL Server real: los cinco roles, el rol efectivo frente al del
/// token, el Id de pertenencia que un técnico necesita, el aislamiento entre empresas y quién puede (y quién no) preguntar.
/// </summary>
public sealed class AccountMeEndToEndTests(PlatformFlowFixture fixture) : IClassFixture<PlatformFlowFixture>
{
    private const string Me = "/api/v1/account/me";

    private readonly PlatformFlowFixture _fixture = fixture;

    private Task<HttpResponseMessage> GetMe(string? token, string query = "") =>
        _fixture.SendAsync(HttpMethod.Get, Me + query, bearer: token);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) => await MfaFlowFixture.JsonOf(response);

    private static string TokenRole(string token) =>
        new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.Single(claim => claim.Type == "role").Value;

    // ===== Quién soy, por rol =====

    [Fact]
    public async Task TheOwner_SeesTheirDataTheirCompanyAndEveryPermission()
    {
        var company = $"Empresa {Guid.NewGuid():N}";
        var owner = await _fixture.RegisterOwnerAsync(company);

        var response = await GetMe(owner.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await BodyOf(response);
        Assert.Equal(owner.Email, me.GetProperty("email").GetString());
        Assert.Equal("Ana Pérez", me.GetProperty("name").GetString());
        Assert.Equal(company, me.GetProperty("tenantName").GetString());
        Assert.Equal("Owner", me.GetProperty("role").GetString());
        Assert.Equal(PermissionNames.For(TenantRole.Owner), me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!));
    }

    [Theory]
    [InlineData(TenantRole.Admin)]
    [InlineData(TenantRole.Manager)]
    [InlineData(TenantRole.Employee)]
    [InlineData(TenantRole.Technician)]
    public async Task EveryRole_GetsA200_WithItsRoleAndExactlyThePermissionsOfTheSingleTable(TenantRole role)
    {
        var owner = await _fixture.RegisterOwnerAsync();
        var member = await _fixture.InviteAndJoinAsync(owner, role);

        var response = await GetMe(member.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await BodyOf(response);
        Assert.Equal(role.ToString(), me.GetProperty("role").GetString());
        Assert.Equal(PermissionNames.For(role), me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!));
        Assert.Contains("Account.Read", me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(member.Email, me.GetProperty("email").GetString());
    }

    // ===== El rol efectivo, no el del token =====

    [Fact]
    public async Task AfterARoleChange_TheSameOldToken_AlreadySaysTheNewRole()
    {
        var owner = await _fixture.RegisterOwnerAsync();
        var technician = await _fixture.InviteAndJoinAsync(owner, TenantRole.Technician);
        var membershipId = await _fixture.TenantUserIdOfAsync(owner, technician);

        var before = await BodyOf(await GetMe(technician.AccessToken));
        Assert.Equal("Technician", before.GetProperty("role").GetString());

        var change = await _fixture.SendAsync(HttpMethod.Put, $"/api/v1/users/{membershipId}/role", new { role = (int)TenantRole.Manager }, owner.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        // El token sigue diciendo Technician (se emitió antes del cambio)...
        Assert.Equal("Technician", TokenRole(technician.AccessToken));

        // ...pero /me ya dice la verdad de la base de datos, con los permisos del rol nuevo.
        var after = await BodyOf(await GetMe(technician.AccessToken));
        Assert.Equal("Manager", after.GetProperty("role").GetString());
        Assert.Contains("Jobs.Assign", after.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.DoesNotContain("Jobs.Assign", before.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
    }

    // ===== El Id de pertenencia =====

    [Fact]
    public async Task ATechnician_CanTellWhichJobsAreTheirs_ByTheMembershipIdOfMe()
    {
        var owner = await _fixture.RegisterOwnerAsync();
        var technician = await _fixture.InviteAndJoinAsync(owner, TenantRole.Technician);
        var membershipId = await _fixture.TenantUserIdOfAsync(owner, technician);

        var customer = await _fixture.SendAsync(HttpMethod.Post, "/api/v1/customers", new { type = 0, name = "Cliente de prueba" }, owner.AccessToken);
        var customerId = (await BodyOf(customer)).GetProperty("id").GetGuid();
        var job = await _fixture.SendAsync(HttpMethod.Post, "/api/v1/jobs", new { customerId, title = "Avería", priority = 1 }, owner.AccessToken);
        var jobId = (await BodyOf(job)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await _fixture.SendAsync(HttpMethod.Post, $"/api/v1/jobs/{jobId}/assign", new { tenantUserId = membershipId }, owner.AccessToken)).StatusCode);

        // El técnico no puede listar el equipo (sin Users.Read): /me es lo que le dice cuál es su Id de pertenencia.
        Assert.Equal(HttpStatusCode.Forbidden, (await _fixture.SendAsync(HttpMethod.Get, "/api/v1/users", bearer: technician.AccessToken)).StatusCode);
        var me = await BodyOf(await GetMe(technician.AccessToken));
        var jobs = await BodyOf(await _fixture.SendAsync(HttpMethod.Get, "/api/v1/jobs", bearer: technician.AccessToken));

        Assert.Equal(membershipId, me.GetProperty("membershipId").GetGuid());
        Assert.Equal(me.GetProperty("membershipId").GetGuid(), jobs.EnumerateArray().Single().GetProperty("assignedTenantUserId").GetGuid());
        Assert.NotEqual(me.GetProperty("userId").GetGuid(), me.GetProperty("membershipId").GetGuid()); // no es el Id de la cuenta (el sub)
    }

    // ===== Dos empresas =====

    [Fact]
    public async Task TwoCompanies_EachOwnerSeesOnlyTheirOwn()
    {
        var nameA = $"Empresa A {Guid.NewGuid():N}";
        var nameB = $"Empresa B {Guid.NewGuid():N}";
        var a = await _fixture.RegisterOwnerAsync(nameA);
        var b = await _fixture.RegisterOwnerAsync(nameB);

        var meA = await BodyOf(await GetMe(a.AccessToken));
        var meB = await BodyOf(await GetMe(b.AccessToken));

        Assert.Equal((nameA, a.Email), (meA.GetProperty("tenantName").GetString(), meA.GetProperty("email").GetString()));
        Assert.Equal((nameB, b.Email), (meB.GetProperty("tenantName").GetString(), meB.GetProperty("email").GetString()));
        Assert.NotEqual(meA.GetProperty("tenantId").GetGuid(), meB.GetProperty("tenantId").GetGuid());
        Assert.DoesNotContain(nameB, await (await GetMe(a.AccessToken)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AParameterNamingAnotherPersonOrCompany_IsIgnored()
    {
        var a = await _fixture.RegisterOwnerAsync($"Empresa {Guid.NewGuid():N}");
        var b = await _fixture.RegisterOwnerAsync($"Empresa {Guid.NewGuid():N}");
        var meB = await BodyOf(await GetMe(b.AccessToken));

        var response = await GetMe(a.AccessToken, $"?userId={meB.GetProperty("userId").GetGuid()}&tenantId={meB.GetProperty("tenantId").GetGuid()}&email={b.Email}");

        var me = await BodyOf(response);
        Assert.Equal(a.Email, me.GetProperty("email").GetString());
        Assert.NotEqual(meB.GetProperty("tenantId").GetGuid(), me.GetProperty("tenantId").GetGuid());
    }

    // ===== Quién puede preguntar =====

    [Fact]
    public async Task WithoutAToken_WithGarbage_OrWithAPlatformSession_ItIsA401()
    {
        var platformToken = await _fixture.PlatformTokenAsync(await _fixture.CreateOperatorAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMe(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMe("esto-no-es-un-token")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMe(platformToken)).StatusCode); // otro mundo: el operador no es una persona de empresa
    }

    [Fact]
    public async Task AMemberOfASuspendedCompany_GetsTheSuspensionError_NotTheirData()
    {
        var owner = await _fixture.RegisterOwnerAsync($"Empresa {Guid.NewGuid():N}");
        var tenantId = (await BodyOf(await GetMe(owner.AccessToken))).GetProperty("tenantId").GetGuid();
        var platformToken = await _fixture.PlatformTokenAsync(await _fixture.CreateOperatorAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await _fixture.SendAsync(HttpMethod.Post, $"/api/v1/platform/tenants/{tenantId}/suspend", bearer: platformToken)).StatusCode);

        var response = await GetMe(owner.AccessToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tenant.suspended", (await BodyOf(response)).GetProperty("code").GetString());
        Assert.DoesNotContain(owner.Email, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AMemberRemovedThroughTheApi_WithAStillUnexpiredToken_GetsNothing()
    {
        // Hasta la spec 015 daba un 403. La baja borra los refresh tokens de la persona, y ahora un token sin sesión activa se rechaza
        // ANTES de la autorización: 401.
        var owner = await _fixture.RegisterOwnerAsync();
        var member = await _fixture.InviteAndJoinAsync(owner, TenantRole.Employee);
        var membershipId = await _fixture.TenantUserIdOfAsync(owner, member);
        Assert.Equal(HttpStatusCode.OK, (await GetMe(member.AccessToken)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _fixture.SendAsync(HttpMethod.Delete, $"/api/v1/users/{membershipId}", bearer: owner.AccessToken)).StatusCode);

        var response = await GetMe(member.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(member.Email, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AMembershipRemovedButTheSessionAlive_StillGetsA403_AndNothing()
    {
        // La revalidación de la pertenencia (ADR-013) sigue siendo la segunda barrera: sesión activa, persona que ya no es miembro.
        var owner = await _fixture.RegisterOwnerAsync();
        var member = await _fixture.InviteAndJoinAsync(owner, TenantRole.Employee);
        Assert.Equal(HttpStatusCode.OK, (await GetMe(member.AccessToken)).StatusCode);
        await _fixture.RemoveMembershipKeepingSessionsAsync(member);

        var response = await GetMe(member.AccessToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(member.Email, await response.Content.ReadAsStringAsync());
    }

    // ===== Qué lleva la respuesta =====

    [Fact]
    public async Task TheAnswer_IsNeverCached_AndHasExactlyTheEightFields_WithNothingSensitive()
    {
        // Con el segundo factor activo hay secretos en la base de datos: ninguno debe salir. Activarlo cierra las sesiones (también la de
        // este token, spec 015), así que se entra de nuevo con el segundo paso para tener un token válido.
        var owner = await _fixture.NewMfaAccountAsync();

        var response = await GetMe(owner.AccessToken);

        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(
            ["email", "membershipId", "name", "permissions", "role", "tenantId", "tenantName", "userId"],
            JsonDocument.Parse(body).RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        foreach (var forbidden in new[] { "password", "hash", "secret", "recovery", "token", "cipher" })
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }
    }
}
