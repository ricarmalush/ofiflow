using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// Las rutas del segundo factor por HTTP (spec 010, bloque 6), con la API real y SQL Server real: que existen, que exigen
/// sesión (salvo canjear el desafío), qué estado devuelven, que las respuestas con secretos no se guardan en caché y que
/// cada error lleva su código estable. El ciclo completo, con códigos TOTP reales y cada ataque, está en el bloque 7.
/// Cada petición declara una IP distinta: los límites por IP son otra defensa y aquí no deben intervenir.
/// </summary>
public class MfaEndpointsTests(MembersFixture fixture) : IClassFixture<MembersFixture>
{
    private sealed record Problem(int? Status, string? Title, string? Detail, string? Code);

    private static HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, TestClientIpStartupFilter.NewIp());

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private sealed record NewOwner(HttpClient Client, string Password);

    /// <summary>
    /// Una empresa nueva con su Owner ya autenticado. Registra y entra desde una IP propia: el registro admite 3 por hora y
    /// por IP, y todos los clientes de prueba comparten una (el límite real se prueba aparte).
    /// </summary>
    private async Task<NewOwner> NewOwnerAsync(string company)
    {
        var email = $"mfa-{Guid.NewGuid():N}@example.com";
        var password = MembersFixture.NewPassword();

        var register = await fixture.Anonymous.SendAsync(Request(HttpMethod.Post, "/api/v1/auth/register", new { companyName = company, userName = "Ana", email, password }));
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);

        var login = await fixture.Anonymous.SendAsync(Request(HttpMethod.Post, "/api/v1/auth/login", new { email, password }));
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();

        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());

        return new NewOwner(client, password);
    }

    private static async Task<Problem> ReadProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Problem>())!;

    // ===== Exigen sesión, salvo canjear el desafío =====

    [Theory]
    [InlineData("GET", "/api/v1/account/mfa")]
    [InlineData("POST", "/api/v1/account/mfa/setup")]
    [InlineData("POST", "/api/v1/account/mfa/enable")]
    [InlineData("POST", "/api/v1/account/mfa/disable")]
    [InlineData("POST", "/api/v1/account/mfa/recovery-codes")]
    [InlineData("DELETE", "/api/v1/users/00000000-0000-0000-0000-000000000001/mfa")]
    public async Task EveryMfaRoute_RequiresASession(string method, string path)
    {
        var request = Request(new HttpMethod(method), path, method == "POST" ? new { password = "x", code = "123456" } : null);

        var response = await fixture.Anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RedeemingAChallenge_DoesNotRequireASession_AndEveryFailureLooksTheSame()
    {
        // Anónima: sin sesión responde 401 (el desafío no sirve), no 401 de "falta el token" ni 404. Y cualquier forma de
        // fallo —inventado, con la forma equivocada, código mal escrito— da exactamente la misma respuesta (spec 010 R13).
        var unknownButWellFormed = new { challenge = "A".PadRight(43, 'A'), code = "123456" };
        var malformedChallenge = new { challenge = "no-es-un-desafio", code = "123456" };
        var malformedCode = new { challenge = "A".PadRight(43, 'A'), code = "no es un código" };

        var fingerprints = new List<string>();
        foreach (var body in new object[] { unknownButWellFormed, malformedChallenge, malformedCode })
        {
            var response = await fixture.Anonymous.SendAsync(Request(HttpMethod.Post, "/api/v1/auth/mfa/verify", body));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            fingerprints.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.Single(fingerprints.Distinct()); // el cuerpo es idéntico en los tres
    }

    [Fact]
    public async Task RedeemingAChallenge_WithAnAbsurdlyLongInput_IsAValidationError_NotSomethingToHash()
    {
        var response = await fixture.Anonymous.SendAsync(
            Request(HttpMethod.Post, "/api/v1/auth/mfa/verify", new { challenge = new string('a', 5000), code = "123456" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ===== Los cinco roles gestionan su propio segundo factor =====

    [Theory]
    [InlineData(TenantRole.Admin)]
    [InlineData(TenantRole.Manager)]
    [InlineData(TenantRole.Employee)]
    [InlineData(TenantRole.Technician)]
    public async Task EveryRole_CanSeeTheStatusOfItsOwnAccount(TenantRole role)
    {
        var member = await fixture.SeedMemberAsync(role);

        var response = await member.Client.SendAsync(Request(HttpMethod.Get, "/api/v1/account/mfa"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.GetProperty("setupPending").GetBoolean());
        Assert.Equal(0, status.GetProperty("recoveryCodesRemaining").GetInt32());
    }

    [Fact]
    public async Task TheOwner_CanSeeTheStatus_Too()
    {
        var response = await fixture.OwnerClient.SendAsync(Request(HttpMethod.Get, "/api/v1/account/mfa"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheStatus_NeverContainsASecretOrACode()
    {
        var response = await fixture.OwnerClient.SendAsync(Request(HttpMethod.Get, "/api/v1/account/mfa"));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(["enabled", "setupPending", "recoveryCodesRemaining"],
            JsonDocument.Parse(text).RootElement.EnumerateObject().Select(p => p.Name));
    }

    // ===== Pedir el secreto =====

    [Fact]
    public async Task AskingForTheSecret_WithTheRightPassword_ReturnsItOnce_AndNeverCachesIt()
    {
        var owner = await NewOwnerAsync("Empresa del secreto");

        var response = await owner.Client.SendAsync(Request(HttpMethod.Post, "/api/v1/account/mfa/setup", new { password = owner.Password }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString()); // el secreto no se vuelve a mostrar: ninguna caché lo guarda

        var setup = await response.Content.ReadFromJsonAsync<JsonElement>();
        var secret = setup.GetProperty("secret").GetString()!;
        Assert.Equal(32, secret.Length);
        Assert.StartsWith("otpauth://totp/OfiFlow:", setup.GetProperty("otpAuthUri").GetString());
        Assert.Contains($"secret={secret}", setup.GetProperty("otpAuthUri").GetString());

        // Queda como PENDIENTE: aún no cuenta como activo.
        var status = await (await owner.Client.SendAsync(Request(HttpMethod.Get, "/api/v1/account/mfa"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.True(status.GetProperty("setupPending").GetBoolean());
        Assert.DoesNotContain(secret, status.ToString()); // y el estado nunca lo repite
    }

    [Fact]
    public async Task AskingForTheSecret_WithAWrongPassword_IsRefusedWithItsStableCode()
    {
        var owner = await NewOwnerAsync("Empresa de la contraseña mala");

        var response = await owner.Client.SendAsync(Request(HttpMethod.Post, "/api/v1/account/mfa/setup", new { password = "incorrecta" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("mfa.invalid_password", (await ReadProblemAsync(response)).Code);
        Assert.NotEqual("no-store", response.Headers.CacheControl?.ToString()); // un error no lleva el no-store de un secreto
    }

    [Fact]
    public async Task AskingForTheSecret_WithAnEmptyPassword_IsAValidationError()
    {
        var response = await fixture.OwnerClient.SendAsync(Request(HttpMethod.Post, "/api/v1/account/mfa/setup", new { password = "" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation.failed", (await ReadProblemAsync(response)).Code);
    }

    // ===== Confirmar, desactivar y regenerar sin haber activado nada =====

    [Fact]
    public async Task Confirming_WithoutHavingAskedForASecret_SaysSoWithItsCode()
    {
        var owner = await NewOwnerAsync("Empresa sin secreto");

        var response = await owner.Client.SendAsync(Request(HttpMethod.Post, "/api/v1/account/mfa/enable", new { code = "123456" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("mfa.setup_required", (await ReadProblemAsync(response)).Code);
    }

    [Fact]
    public async Task Confirming_WithAWrongCode_LeavesTheSetupPending()
    {
        var owner = await NewOwnerAsync("Empresa del código malo");
        await owner.Client.SendAsync(Request(HttpMethod.Post, "/api/v1/account/mfa/setup", new { password = owner.Password }));

        var response = await owner.Client.SendAsync(Request(HttpMethod.Post, "/api/v1/account/mfa/enable", new { code = "000000" }));

        // Un código erróneo casi siempre es inválido; si por azar coincidiera con el real (1 entre un millón), el test no sería fiable.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("mfa.invalid_code", (await ReadProblemAsync(response)).Code);

        var status = await (await owner.Client.SendAsync(Request(HttpMethod.Get, "/api/v1/account/mfa"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.True(status.GetProperty("setupPending").GetBoolean());
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData("")]
    public async Task Confirming_WithACodeThatIsNotSixDigits_IsRejectedBeforeAnythingIsComputed(string code)
    {
        var response = await fixture.OwnerClient.SendAsync(Request(HttpMethod.Post, "/api/v1/account/mfa/enable", new { code }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation.failed", (await ReadProblemAsync(response)).Code);
    }

    [Fact]
    public async Task Disabling_AndRegenerating_WhenThereIsNoMfa_SayItIsNotEnabled()
    {
        var owner = await NewOwnerAsync("Empresa sin MFA");

        var disable = await owner.Client.SendAsync(Request(HttpMethod.Post, "/api/v1/account/mfa/disable", new { password = owner.Password, code = "123456" }));
        var regenerate = await owner.Client.SendAsync(Request(HttpMethod.Post, "/api/v1/account/mfa/recovery-codes", new { password = owner.Password, code = "123456" }));

        Assert.Equal(HttpStatusCode.BadRequest, disable.StatusCode);
        Assert.Equal("mfa.not_enabled", (await ReadProblemAsync(disable)).Code);
        Assert.Equal(HttpStatusCode.BadRequest, regenerate.StatusCode);
        Assert.Equal("mfa.not_enabled", (await ReadProblemAsync(regenerate)).Code);
    }

    [Fact]
    public async Task Disabling_WithAWrongPassword_IsRefusedBeforeAnythingIsLookedAt()
    {
        var owner = await NewOwnerAsync("Empresa de otra contraseña");

        var response = await owner.Client.SendAsync(Request(HttpMethod.Post, "/api/v1/account/mfa/disable", new { password = "incorrecta", code = "123456" }));

        Assert.Equal("mfa.invalid_password", (await ReadProblemAsync(response)).Code);
    }

    // ===== Reiniciar el segundo factor de otro miembro =====

    [Fact]
    public async Task TheOwner_CanResetAMembersMfa_AndItIsIdempotent()
    {
        var member = await fixture.SeedMemberAsync(TenantRole.Technician);

        var first = await fixture.OwnerClient.SendAsync(Request(HttpMethod.Delete, $"/api/v1/users/{member.TenantUserId}/mfa"));
        var second = await fixture.OwnerClient.SendAsync(Request(HttpMethod.Delete, $"/api/v1/users/{member.TenantUserId}/mfa"));

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode); // repetirlo no falla
    }

    [Theory]
    [InlineData(TenantRole.Manager)]
    [InlineData(TenantRole.Employee)]
    [InlineData(TenantRole.Technician)]
    public async Task RolesWithoutUsersManage_CannotResetAnyonesMfa(TenantRole actor)
    {
        var someone = await fixture.SeedMemberAsync(TenantRole.Employee);
        var member = await fixture.SeedMemberAsync(actor);

        var response = await member.Client.SendAsync(Request(HttpMethod.Delete, $"/api/v1/users/{someone.TenantUserId}/mfa"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnAdmin_CannotResetAnotherAdminsMfa_NorTheOwners()
    {
        var admin = await fixture.SeedMemberAsync(TenantRole.Admin);
        var otherAdmin = await fixture.SeedMemberAsync(TenantRole.Admin);

        var againstAdmin = await admin.Client.SendAsync(Request(HttpMethod.Delete, $"/api/v1/users/{otherAdmin.TenantUserId}/mfa"));
        Assert.Equal(HttpStatusCode.Forbidden, againstAdmin.StatusCode);
        Assert.Equal("tenant_user.role_not_manageable", (await ReadProblemAsync(againstAdmin)).Code);

        // Control positivo: el mismo Admin sí puede con un rol que gestiona.
        var technician = await fixture.SeedMemberAsync(TenantRole.Technician);
        var againstTechnician = await admin.Client.SendAsync(Request(HttpMethod.Delete, $"/api/v1/users/{technician.TenantUserId}/mfa"));
        Assert.Equal(HttpStatusCode.NoContent, againstTechnician.StatusCode);
    }

    [Fact]
    public async Task NobodyResetsTheirOwnMfa_ByThisRoute()
    {
        var admin = await fixture.SeedMemberAsync(TenantRole.Admin);

        var response = await admin.Client.SendAsync(Request(HttpMethod.Delete, $"/api/v1/users/{admin.TenantUserId}/mfa"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tenant_user.cannot_manage_self", (await ReadProblemAsync(response)).Code);
    }

    [Fact]
    public async Task ResettingAnUnknownMember_Is404_WithTheSameCodeAsAMemberOfAnotherCompany()
    {
        var other = await fixture.RegisterCompanyAsync("Otra empresa para el reinicio");
        var foreign = await fixture.SeedMemberAsync(TenantRole.Technician, other.TenantId);

        var unknown = await fixture.OwnerClient.SendAsync(Request(HttpMethod.Delete, $"/api/v1/users/{Guid.NewGuid()}/mfa"));
        var ofAnotherCompany = await fixture.OwnerClient.SendAsync(Request(HttpMethod.Delete, $"/api/v1/users/{foreign.TenantUserId}/mfa"));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ofAnotherCompany.StatusCode);
        var unknownProblem = await ReadProblemAsync(unknown);
        var foreignProblem = await ReadProblemAsync(ofAnotherCompany);

        Assert.Equal("tenant_user.not_found", unknownProblem.Code);
        Assert.Equal(unknownProblem.Code, foreignProblem.Code); // el mismo código y el mismo mensaje: no se distingue
        Assert.Equal(unknownProblem.Detail, foreignProblem.Detail);
    }
}
