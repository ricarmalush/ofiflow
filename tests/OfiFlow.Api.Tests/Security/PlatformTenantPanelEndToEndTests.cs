using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Platform;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// El panel del operador de punta a punta (spec 011, bloque 6), con la API entera y SQL Server real: listar solo da metadatos (se
/// inspecciona el cuerpo ENTERO), suspender y reactivar cortan y devuelven el acceso de los miembros sin tocar sus datos, una empresa
/// no arrastra a otra, y solo vale una sesión de plataforma de un operador activo.
/// </summary>
public sealed class PlatformTenantPanelEndToEndTests(PlatformFlowFixture fixture) : IClassFixture<PlatformFlowFixture>
{
    private const string Route = "/api/v1/platform/tenants";

    private readonly PlatformFlowFixture _fixture = fixture;

    private sealed record Company(MfaFlowFixture.Account Owner, Guid TenantId, string CompanyName, string CustomerName);

    private async Task<string> OperatorTokenAsync() => await _fixture.PlatformTokenAsync(await _fixture.CreateOperatorAsync());

    private async Task<Company> NewCompanyAsync()
    {
        var name = $"Empresa {Guid.NewGuid():N}";
        var owner = await _fixture.RegisterOwnerAsync(name);
        var tenantId = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(owner.AccessToken).Claims.Single(c => c.Type == "tenant_id").Value);

        // Un cliente con datos personales: nada de esto puede aparecer jamás en lo que ve el operador.
        var customerName = $"Cliente-{Guid.NewGuid():N}";
        var created = await _fixture.SendAsync(HttpMethod.Post, "/api/v1/customers",
            new { type = 0, name = customerName, email = $"{customerName}@example.com", phone = "600123456", address = "Calle Secreta 1" }, owner.AccessToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return new Company(owner, tenantId, name, customerName);
    }

    private Task<HttpResponseMessage> ListAsync(string token, string query = "") =>
        _fixture.SendAsync(HttpMethod.Get, Route + query, bearer: token);

    private Task<HttpResponseMessage> SuspendAsync(string token, Guid tenantId) =>
        _fixture.SendAsync(HttpMethod.Post, $"{Route}/{tenantId}/suspend", bearer: token);

    private Task<HttpResponseMessage> ReactivateAsync(string token, Guid tenantId) =>
        _fixture.SendAsync(HttpMethod.Post, $"{Route}/{tenantId}/reactivate", bearer: token);

    private static async Task<string> CodeOfAsync(HttpResponseMessage response) =>
        (await MfaFlowFixture.JsonOf(response)).GetProperty("code").GetString()!;

    private async Task<List<PlatformAuditRow>> AuditOfAsync(Guid tenantId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Set<PlatformAuditEntry>().AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.OccurredAt)
            .Select(e => new PlatformAuditRow(e.OperatorUserId, e.Action))
            .ToListAsync();
    }

    private sealed record PlatformAuditRow(Guid OperatorUserId, string Action);

    private static JsonElement ItemOf(JsonElement page, Guid tenantId) =>
        page.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == tenantId);

    // ===== Listar =====

    [Fact]
    public async Task TheList_HasOnlyTheFiveMetadataFields_AndNoPersonalDataAnywhereInTheBody()
    {
        var company = await NewCompanyAsync();
        var token = await OperatorTokenAsync();

        var response = await ListAsync(token, "?pageSize=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var page = JsonDocument.Parse(body).RootElement;

        // La forma: la página y, por empresa, exactamente cinco campos.
        Assert.Equal(["items", "page", "pageSize", "totalCount"], page.EnumerateObject().Select(p => p.Name).Order());
        foreach (var item in page.GetProperty("items").EnumerateArray())
        {
            Assert.Equal(["createdAt", "id", "memberCount", "name", "suspendedAt"], item.EnumerateObject().Select(p => p.Name).Order());
        }

        var mine = ItemOf(page, company.TenantId);
        Assert.Equal(company.CompanyName, mine.GetProperty("name").GetString());
        Assert.Equal(1, mine.GetProperty("memberCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("suspendedAt").ValueKind);

        // El cuerpo ENTERO: ni el email ni el nombre de la persona, ni nada del cliente.
        Assert.DoesNotContain(company.Owner.Email, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ana Pérez", body);
        Assert.DoesNotContain(company.CustomerName, body);
        Assert.DoesNotContain("600123456", body);
        Assert.DoesNotContain("Calle Secreta", body);
        Assert.DoesNotContain("@example.com", body);
    }

    [Fact]
    public async Task TheList_IsPaged_WithTheRequestedSize_AndKeepsTheTotal()
    {
        await NewCompanyAsync();
        await NewCompanyAsync();
        var token = await OperatorTokenAsync();

        var page = await MfaFlowFixture.JsonOf(await ListAsync(token, "?page=1&pageSize=1"));

        Assert.Equal(1, page.GetProperty("items").GetArrayLength());
        Assert.Equal((1, 1), (page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32()));
        Assert.True(page.GetProperty("totalCount").GetInt32() >= 2);
    }

    [Theory]
    [InlineData("?pageSize=101")]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=-1")]
    [InlineData("?pageSize=2147483647")]
    [InlineData("?page=0")]
    [InlineData("?page=-3")]
    [InlineData("?page=2147483647")]
    public async Task AnInvalidPage_IsAValidationError_WithTheStableCode(string query)
    {
        var token = await OperatorTokenAsync();

        var response = await ListAsync(token, query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
    }

    [Theory]
    [InlineData("?pageSize=abc")]
    [InlineData("?page=uno")]
    [InlineData("?suspended=quizas")]
    public async Task ANonNumericParameter_IsABadRequest_NotAServerError(string query)
    {
        var token = await OperatorTokenAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await ListAsync(token, query)).StatusCode);
    }

    [Fact]
    public async Task TheSuspendedFilter_ListsOnlyTheSuspendedOnes()
    {
        var company = await NewCompanyAsync();
        var token = await OperatorTokenAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await SuspendAsync(token, company.TenantId)).StatusCode);

        var suspended = await MfaFlowFixture.JsonOf(await ListAsync(token, "?suspended=true&pageSize=100"));
        var active = await MfaFlowFixture.JsonOf(await ListAsync(token, "?suspended=false&pageSize=100"));

        Assert.NotEqual(JsonValueKind.Null, ItemOf(suspended, company.TenantId).GetProperty("suspendedAt").ValueKind);
        Assert.DoesNotContain(active.GetProperty("items").EnumerateArray(), item => item.GetProperty("id").GetGuid() == company.TenantId);
        Assert.All(suspended.GetProperty("items").EnumerateArray(), item => Assert.NotEqual(JsonValueKind.Null, item.GetProperty("suspendedAt").ValueKind));
    }

    // ===== Suspender y reactivar: el efecto en los miembros =====

    [Fact]
    public async Task Suspending_CutsTheMembersOff_AndReactivatingGivesThemBack_WithTheirDataIntact()
    {
        var company = await NewCompanyAsync();
        var token = await OperatorTokenAsync();
        Assert.Equal(HttpStatusCode.OK, (await _fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: company.Owner.AccessToken)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await SuspendAsync(token, company.TenantId)).StatusCode);

        // Con el token que ya tenía: 403 en la SIGUIENTE petición, con el código estable.
        var denied = await _fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: company.Owner.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("tenant.suspended", await CodeOfAsync(denied));

        // Y no puede abrir una sesión nueva: contraseña correcta, 403 con el mismo código.
        var login = await _fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = company.Owner.Email, password = company.Owner.Password });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("tenant.suspended", await CodeOfAsync(login));

        Assert.Equal(HttpStatusCode.NoContent, (await ReactivateAsync(token, company.TenantId)).StatusCode);

        // Reactivada: la misma sesión de antes vuelve a valer y el cliente sigue ahí.
        var customers = await _fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: company.Owner.AccessToken);
        Assert.Equal(HttpStatusCode.OK, customers.StatusCode);
        Assert.Contains(company.CustomerName, await customers.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SuspendingOneCompany_DoesNotAffectAnotherInAnything()
    {
        var a = await NewCompanyAsync();
        var b = await NewCompanyAsync();
        var token = await OperatorTokenAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await SuspendAsync(token, a.TenantId)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await _fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: b.Owner.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = b.Owner.Email, password = b.Owner.Password })).StatusCode);
        Assert.Equal(JsonValueKind.Null, ItemOf(await MfaFlowFixture.JsonOf(await ListAsync(token, "?pageSize=100")), b.TenantId).GetProperty("suspendedAt").ValueKind);
    }

    [Fact]
    public async Task SuspendingTwice_OrReactivatingAnActiveOne_IsABusinessErrorWithItsCode()
    {
        var company = await NewCompanyAsync();
        var token = await OperatorTokenAsync();

        var reactivateActive = await ReactivateAsync(token, company.TenantId);
        Assert.Equal(HttpStatusCode.BadRequest, reactivateActive.StatusCode);
        Assert.Equal("tenant.not_suspended", await CodeOfAsync(reactivateActive));

        Assert.Equal(HttpStatusCode.NoContent, (await SuspendAsync(token, company.TenantId)).StatusCode);
        var again = await SuspendAsync(token, company.TenantId);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("tenant.already_suspended", await CodeOfAsync(again));

        // Los errores no escribieron nada más: una sola suspensión y ninguna reactivación.
        Assert.Equal([PlatformAuditActions.TenantSuspended], (await AuditOfAsync(company.TenantId)).Select(row => row.Action));
    }

    [Fact]
    public async Task ATenantThatDoesNotExist_IsTheSame404OnBothOperations()
    {
        var token = await OperatorTokenAsync();
        var missing = Guid.NewGuid();

        var suspend = await SuspendAsync(token, missing);
        var reactivate = await ReactivateAsync(token, missing);

        Assert.Equal(HttpStatusCode.NotFound, suspend.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, reactivate.StatusCode);
        Assert.Equal("tenant.not_found", await CodeOfAsync(suspend));
        Assert.Equal("tenant.not_found", await CodeOfAsync(reactivate));
        Assert.Empty(await AuditOfAsync(missing));
    }

    [Fact]
    public async Task AMalformedTenantId_IsNeverAServerError()
    {
        var token = await OperatorTokenAsync();

        var response = await _fixture.SendAsync(HttpMethod.Post, $"{Route}/no-es-un-guid/suspend", bearer: token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ===== Auditoría y eventos =====

    [Fact]
    public async Task EachChange_LeavesOneAuditRowWithTheOperator_AndTheEvents1604And1605WithOnlyIds()
    {
        var company = await NewCompanyAsync();
        var platformOperator = await _fixture.CreateOperatorAsync();
        var token = await _fixture.PlatformTokenAsync(platformOperator);

        Assert.Equal(HttpStatusCode.NoContent, (await SuspendAsync(token, company.TenantId)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ReactivateAsync(token, company.TenantId)).StatusCode);

        var rows = await AuditOfAsync(company.TenantId);
        // Sin depender del orden: el reloj del fixture está parado, así que las dos filas llevan la misma hora.
        Assert.Equal(new[] { PlatformAuditActions.TenantReactivated, PlatformAuditActions.TenantSuspended }, rows.Select(row => row.Action).Order());
        Assert.All(rows, row => Assert.Equal(platformOperator.UserId, row.OperatorUserId));

        foreach (var eventId in new[] { SecurityEventIds.PlatformTenantSuspended, SecurityEventIds.PlatformTenantReactivated })
        {
            var entry = Assert.Single(_fixture.Logs.Entries, e => e.EventId == eventId && e.Message.Contains(company.TenantId.ToString(), StringComparison.Ordinal));
            Assert.Contains(platformOperator.UserId.ToString(), entry.Message);
            Assert.DoesNotContain(company.CompanyName, entry.Message);
            Assert.DoesNotContain(company.Owner.Email, entry.Message);
        }
    }

    [Fact]
    public async Task Listing_LeavesTheEvent1603_ButNoAuditRow()
    {
        var token = await OperatorTokenAsync();
        var before = _fixture.Logs.Entries.Count(e => e.EventId == SecurityEventIds.PlatformTenantsListed);

        await ListAsync(token);

        Assert.Equal(before + 1, _fixture.Logs.Entries.Count(e => e.EventId == SecurityEventIds.PlatformTenantsListed));

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.DoesNotContain(await db.Set<PlatformAuditEntry>().AsNoTracking().Select(e => e.Action).ToListAsync(), action => action.Contains("list", StringComparison.OrdinalIgnoreCase));
    }

    // ===== Quién puede entrar =====

    [Fact]
    public async Task EveryPanelRoute_RejectsRequestsWithoutAPlatformSession()
    {
        var company = await NewCompanyAsync();
        var target = company.TenantId;

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, Route),
                     (HttpMethod.Post, $"{Route}/{target}/suspend"),
                     (HttpMethod.Post, $"{Route}/{target}/reactivate")
                 })
        {
            // Sin token, con basura, y con la sesión de un Owner (incluso el de la propia empresa objetivo).
            Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.SendAsync(method, path)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.SendAsync(method, path, bearer: "esto-no-es-un-token")).StatusCode);
            Assert.True(
                (await _fixture.SendAsync(method, path, bearer: company.Owner.AccessToken)).StatusCode == HttpStatusCode.Unauthorized,
                $"{method} {path} aceptó una sesión de empresa.");
        }

        // Y no ocurrió nada: la empresa sigue activa y no hay filas de auditoría.
        Assert.Empty(await AuditOfAsync(target));
        Assert.Equal(HttpStatusCode.OK, (await _fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: company.Owner.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task ADisabledOperator_WithAStillValidToken_CannotListNorSuspend()
    {
        var company = await NewCompanyAsync();
        var platformOperator = await _fixture.CreateOperatorAsync();
        var token = await _fixture.PlatformTokenAsync(platformOperator);
        Assert.Equal(HttpStatusCode.OK, (await ListAsync(token)).StatusCode);

        await _fixture.DisableOperatorAsync(platformOperator);

        Assert.Equal(HttpStatusCode.Forbidden, (await ListAsync(token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SuspendAsync(token, company.TenantId)).StatusCode);
        Assert.Empty(await AuditOfAsync(company.TenantId));
    }

    // ===== Límite de peticiones =====

    [Fact]
    public async Task ThePanelRoutes_AreLimitedToThirtyPerMinutePerIp()
    {
        var token = await OperatorTokenAsync();
        var ip = TestClientIpStartupFilter.NewIp();
        var statuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < 31; attempt++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Route + "?pageSize=1");
            request.Headers.Add(TestClientIpStartupFilter.HeaderName, ip);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            statuses.Add((await _fixture.Http.SendAsync(request)).StatusCode);
        }

        Assert.All(statuses.Take(30), status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[30]);
    }
}
