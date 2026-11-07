using System.Net;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// El segundo factor obligatorio y los cambios de rol, de principio a fin por HTTP y con la regla REAL (spec 022, bloque 2): no se puede desactivar con un rol que lo exige, <c>me</c> dice
/// si hay que activarlo, y promover, degradar, reiniciar o invitar como Admin surten efecto en la siguiente petición con el MISMO token. Cada denegación va con su control positivo.
/// </summary>
public class SecondFactorRoleChangesEndToEndTests(SecondFactorGateFixture fixture) : IClassFixture<SecondFactorGateFixture>
{
    private const string EnrollmentRequired = "mfa.enrollment_required";

    private static string WrongCodeFor(string rightCode) => rightCode == "000000" ? "000001" : "000000";

    private async Task<(HttpStatusCode Status, string? Code)> GetAsync(string path, MfaFlowFixture.Account who)
    {
        var response = await fixture.SendAsync(HttpMethod.Get, path, bearer: who.AccessToken);

        // Un 401 no trae cuerpo (lo rechaza la autenticación antes de llegar a la API): sin cuerpo, no hay código.
        var body = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body))
        {
            return (response.StatusCode, null);
        }

        return (response.StatusCode, System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    private async Task<bool> MustEnrollAsync(MfaFlowFixture.Account who)
    {
        var response = await fixture.SendAsync(HttpMethod.Get, "/api/v1/account/me", bearer: who.AccessToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await MfaFlowFixture.JsonOf(response)).GetProperty("secondFactorEnrollmentRequired").GetBoolean();
    }

    private Task<HttpResponseMessage> DisableAsync(MfaFlowFixture.Account who, string? password = null, string? code = null) =>
        fixture.SendAsync(HttpMethod.Post, "/api/v1/account/mfa/disable", new { password = password ?? who.Password, code = code ?? fixture.FreshCode(who) }, who.AccessToken);

    private async Task ChangeRoleAsync(MfaFlowFixture.Account owner, MfaFlowFixture.Account member, TenantRole role)
    {
        var membershipId = await fixture.TenantUserIdOfAsync(owner, member);
        var change = await fixture.SendAsync(HttpMethod.Put, $"/api/v1/users/{membershipId}/role", new { role = (int)role }, owner.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
    }

    /// <summary>Activa el factor con la cuenta tal como está (aunque esté restringida: es lo que se le permite) y vuelve a entrar con el segundo paso.</summary>
    private async Task EnrollAsync(MfaFlowFixture.Account who)
    {
        await fixture.EnableMfaAsync(who);
        await fixture.LoginWithMfaAsync(who);
    }

    // ===== R7: no se puede desactivar con un rol que lo exige =====

    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    public async Task WhoseRoleRequiresTheFactor_CannotDisableIt_EvenWithTheRightPasswordAndCode(TenantRole role)
    {
        var owner = await fixture.NewMfaAccountAsync();
        var who = role == TenantRole.Owner ? owner : await fixture.InviteAndJoinAsync(owner, TenantRole.Admin);
        if (role == TenantRole.Admin)
        {
            await EnrollAsync(who);
        }

        var response = await DisableAsync(who);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await MfaFlowFixture.JsonOf(response);
        Assert.Equal("mfa.required_for_role", problem.GetProperty("code").GetString());
        Assert.Equal("Tu rol exige el segundo factor, así que no se puede desactivar.", problem.GetProperty("detail").GetString());

        // Nada cambió: el factor sigue activo, la sesión sigue viva y el login sigue pidiendo el segundo paso.
        var status = await MfaFlowFixture.JsonOf(await fixture.SendAsync(HttpMethod.Get, "/api/v1/account/mfa", bearer: who.AccessToken));
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: who.AccessToken)).StatusCode);
        Assert.True((await MfaFlowFixture.JsonOf(await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = who.Email, password = who.Password }))).GetProperty("mfaRequired").GetBoolean());
    }

    [Fact]
    public async Task TheDenial_ComesBeforeThePasswordAndTheCode_SoItNeitherCountsTowardsTheLockoutNorTellsAnything()
    {
        var owner = await fixture.NewMfaAccountAsync();

        // Contraseña y código ERRÓNEOS, repetidos muchas veces más que el límite del bloqueo: siempre el mismo error de rol (no se mira ni una ni otro), y la cuenta NO se bloquea.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var response = await DisableAsync(owner, password: "incorrecta", code: WrongCodeFor(fixture.FreshCode(owner)));
            Assert.Equal("mfa.required_for_role", (await MfaFlowFixture.JsonOf(response)).GetProperty("code").GetString());
        }

        await fixture.LoginWithMfaAsync(owner); // si hubiera contado como intentos fallidos, la cuenta estaría bloqueada
        Assert.Equal(HttpStatusCode.OK, (await fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: owner.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task TheControl_AManagerWithTheFactorCanDisableIt_AsBefore()
    {
        // El mismo recorrido que el de arriba con un rol que NO lo exige: funciona. Sin este control, un «400» podría ser un fallo de la preparación.
        var owner = await fixture.NewMfaAccountAsync();
        var manager = await fixture.InviteAndJoinAsync(owner, TenantRole.Manager);
        await EnrollAsync(manager);

        var response = await DisableAsync(manager);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var login = await MfaFlowFixture.JsonOf(await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = manager.Email, password = manager.Password }));
        Assert.True(login.TryGetProperty("accessToken", out _)); // vuelve el login de un solo paso
    }

    [Fact]
    public async Task WhoWasDemotedFromAdmin_CanDisableIt_WithTheSameToken()
    {
        var owner = await fixture.NewMfaAccountAsync();
        var admin = await fixture.InviteAndJoinAsync(owner, TenantRole.Admin);
        await EnrollAsync(admin);
        Assert.Equal("mfa.required_for_role", (await MfaFlowFixture.JsonOf(await DisableAsync(admin))).GetProperty("code").GetString());

        await ChangeRoleAsync(owner, admin, TenantRole.Manager);

        // El token sigue diciendo «Admin» y aun así, desde la siguiente petición, el rol efectivo (Manager) ya no lo exige.
        Assert.Equal(HttpStatusCode.NoContent, (await DisableAsync(admin)).StatusCode);
    }

    [Fact]
    public async Task WhoWasPromotedToAdmin_CannotDisableIt_WithTheSameToken()
    {
        var owner = await fixture.NewMfaAccountAsync();
        var manager = await fixture.InviteAndJoinAsync(owner, TenantRole.Manager);
        await EnrollAsync(manager);

        await ChangeRoleAsync(owner, manager, TenantRole.Admin);

        var response = await DisableAsync(manager);
        Assert.Equal("mfa.required_for_role", (await MfaFlowFixture.JsonOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheDenialIsLogged_WithTheDedicatedEvent_AndNeverThePasswordNorTheCode()
    {
        var owner = await fixture.NewMfaAccountAsync();
        var before = fixture.Logs.Entries.Count;
        var code = fixture.FreshCode(owner);

        await DisableAsync(owner, code: code);

        var entry = Assert.Single(fixture.Logs.Entries.Skip(before), e => e.EventId == SecurityEventIds.MfaDisableDeniedForRole);
        Assert.Contains("Owner", entry.Message);
        Assert.DoesNotContain(owner.Password, entry.Message);
        Assert.DoesNotContain(code, entry.Message);
        Assert.DoesNotContain(fixture.Logs.Entries.Skip(before), e => e.EventId == SecurityEventIds.MfaDisabled);
    }

    // ===== «Quién soy» dice si hay que activarlo =====

    [Fact]
    public async Task Me_SaysWhetherTheFactorMustBeEnrolled_BeforeAndAfterEnrolling()
    {
        var owner = await fixture.RegisterOwnerAsync();
        Assert.True(await MustEnrollAsync(owner));

        // Con el secreto pedido y sin confirmar, sigue siendo verdad: uno pendiente no protege nada.
        Assert.Equal(HttpStatusCode.OK, (await fixture.SetupAsync(owner)).StatusCode);
        Assert.True(await MustEnrollAsync(owner));

        var enable = await fixture.SendAsync(HttpMethod.Post, "/api/v1/account/mfa/enable", new { code = fixture.CodeNow(owner) }, owner.AccessToken);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.LoginWithMfaAsync(owner);

        Assert.False(await MustEnrollAsync(owner));
    }

    [Theory]
    [InlineData(TenantRole.Manager)]
    [InlineData(TenantRole.Technician)]
    [InlineData(TenantRole.Employee)]
    public async Task Me_SaysNoToTheRolesThatDoNotRequireTheFactor(TenantRole role)
    {
        var owner = await fixture.NewMfaAccountAsync();
        var member = await fixture.InviteAndJoinAsync(owner, role);

        Assert.False(await MustEnrollAsync(member));
    }

    [Fact]
    public async Task Me_FollowsTheEffectiveRole_NotTheTokens()
    {
        var owner = await fixture.NewMfaAccountAsync();
        var technician = await fixture.InviteAndJoinAsync(owner, TenantRole.Technician);
        Assert.False(await MustEnrollAsync(technician));

        await ChangeRoleAsync(owner, technician, TenantRole.Admin);

        Assert.True(await MustEnrollAsync(technician)); // el mismo token, que sigue diciendo Technician
    }

    // ===== R4 y R8: promover, degradar, reiniciar, invitar =====

    [Fact]
    public async Task APromotionToAdmin_RestrictsTheNextRequest_AndEnrollingLiftsIt()
    {
        var owner = await fixture.NewMfaAccountAsync();
        var technician = await fixture.InviteAndJoinAsync(owner, TenantRole.Technician);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/v1/account/sessions", technician)).Status);

        await ChangeRoleAsync(owner, technician, TenantRole.Admin);

        Assert.Equal((HttpStatusCode.Forbidden, EnrollmentRequired), await GetAsync("/api/v1/account/sessions", technician));
        await EnrollAsync(technician);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/v1/customers", technician)).Status);
    }

    [Fact]
    public async Task ADemotionFromAdmin_GivesTheAccessBack_WithoutEnrolling()
    {
        var owner = await fixture.NewMfaAccountAsync();
        var admin = await fixture.InviteAndJoinAsync(owner, TenantRole.Admin);
        Assert.Equal((HttpStatusCode.Forbidden, EnrollmentRequired), await GetAsync("/api/v1/customers", admin));

        await ChangeRoleAsync(owner, admin, TenantRole.Manager);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/v1/customers", admin)).Status); // el mismo token, ya sin la obligación
        Assert.False(await MustEnrollAsync(admin));
    }

    [Fact]
    public async Task WhoseFactorIsResetByAManager_IsRestrictedOnTheNextSession_UntilEnrollingAgain()
    {
        var owner = await fixture.NewMfaAccountAsync();
        var admin = await fixture.InviteAndJoinAsync(owner, TenantRole.Admin);
        await EnrollAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/v1/customers", admin)).Status);

        var membershipId = await fixture.TenantUserIdOfAsync(owner, admin);
        var reset = await fixture.SendAsync(HttpMethod.Delete, $"/api/v1/users/{membershipId}/mfa", bearer: owner.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        // Sus sesiones se cerraron (spec 010): vuelve a entrar con la contraseña sola y su sesión nueva nace restringida.
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync("/api/v1/customers", admin)).Status);
        await fixture.LoginPlainAsync(admin);
        Assert.Equal((HttpStatusCode.Forbidden, EnrollmentRequired), await GetAsync("/api/v1/customers", admin));
        Assert.True(await MustEnrollAsync(admin));

        await EnrollAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/v1/customers", admin)).Status);
    }

    [Fact]
    public async Task AnAdmin_CannotResetAnotherAdminsFactor_TheHierarchyStillDecidesWhoCan()
    {
        // R8: la jerarquía de la spec 009 sigue mandando. Un Admin no puede reiniciar el factor de otro Admin (solo el Owner puede con un Admin): la puerta no cambia quién puede.
        var owner = await fixture.NewMfaAccountAsync();
        var adminA = await fixture.InviteAndJoinAsync(owner, TenantRole.Admin);
        var adminB = await fixture.InviteAndJoinAsync(owner, TenantRole.Admin);
        await EnrollAsync(adminA);
        await EnrollAsync(adminB);
        var membershipB = await fixture.TenantUserIdOfAsync(owner, adminB);

        var reset = await fixture.SendAsync(HttpMethod.Delete, $"/api/v1/users/{membershipB}/mfa", bearer: adminA.AccessToken);

        Assert.Equal(HttpStatusCode.Forbidden, reset.StatusCode);
        Assert.Equal("tenant_user.role_not_manageable", (await MfaFlowFixture.JsonOf(reset)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task WhoIsInvitedAsAdmin_StartsTheirFirstSessionRestricted_AndMeSaysSo()
    {
        var owner = await fixture.NewMfaAccountAsync();

        var admin = await fixture.InviteAndJoinAsync(owner, TenantRole.Admin);

        Assert.True(await MustEnrollAsync(admin));
        Assert.Equal((HttpStatusCode.Forbidden, EnrollmentRequired), await GetAsync("/api/v1/customers", admin));
        Assert.Equal(HttpStatusCode.OK, (await fixture.SetupAsync(admin)).StatusCode);
    }
}
