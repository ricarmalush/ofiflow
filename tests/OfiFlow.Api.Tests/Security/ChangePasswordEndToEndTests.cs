using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Mail;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// Cambiar la contraseña estando dentro por HTTP, de principio a fin (spec 019, bloque 2), con la API entera y SQL Server real: lo que cambia (la contraseña, las demás sesiones, el contador), lo que NO
/// cambia (la sesión actual, el segundo factor, las cuentas de otros), el orden de los errores, el bloqueo de cuenta, el límite de peticiones y que ninguna contraseña ni código llegue a un registro.
/// Cada rechazo va con su control positivo. Cada petición lleva su propia IP de prueba salvo las del límite.
/// </summary>
public class ChangePasswordEndToEndTests(PlatformFlowFixture fixture) : IClassFixture<PlatformFlowFixture>
{
    private const string PasswordRoute = "/api/v1/account/password";
    private const string SessionsRoute = "/api/v1/account/sessions";

    private static string NewSecret() => MembersFixture.NewPassword();

    private Task<HttpResponseMessage> ChangeAsync(string? bearer, string current, string next, string? code = null) =>
        fixture.SendAsync(HttpMethod.Put, PasswordRoute, code is null ? new { currentPassword = current, newPassword = next } : new { currentPassword = current, newPassword = next, code }, bearer);

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;

    private async Task<HttpStatusCode> LoginAsync(string email, string password) =>
        (await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password })).StatusCode;

    private async Task<HttpStatusCode> MeAsync(string token) =>
        (await fixture.SendAsync(HttpMethod.Get, "/api/v1/account/me", bearer: token)).StatusCode;

    private async Task<HttpStatusCode> RefreshAsync(string refreshToken) =>
        (await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken })).StatusCode;

    /// <summary>El login con esa contraseña no da sesión: devuelve un desafío (el segundo factor sigue activo).</summary>
    private async Task AssertLoginAsksForTheCodeAsync(string email, string password)
    {
        var login = await MfaFlowFixture.JsonOf(await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password }));
        Assert.True(login.GetProperty("mfaRequired").GetBoolean());
        Assert.False(login.TryGetProperty("accessToken", out _));
    }

    private async Task<int> ActiveSessionsAsync(string token) =>
        (await MfaFlowFixture.JsonOf(await fixture.SendAsync(HttpMethod.Get, SessionsRoute, bearer: token))).GetArrayLength();

    private static string UserIdOf(string accessToken) => new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Subject;

    /// <summary>Un segundo dispositivo de la misma cuenta: devuelve su access y su refresh token, y deja en la cuenta los de la sesión original.</summary>
    private async Task<(string Access, string Refresh)> SecondDeviceAsync(MfaFlowFixture.Account account)
    {
        var (originalAccess, originalRefresh) = (account.AccessToken, account.RefreshToken);
        // Una cuenta con segundo factor (la que tiene un secreto) entra en dos pasos.
        if (account.Secret.Length > 0)
        {
            await fixture.LoginWithMfaAsync(account);
        }
        else
        {
            await fixture.LoginPlainAsync(account);
        }

        var second = (account.AccessToken, account.RefreshToken);
        (account.AccessToken, account.RefreshToken) = (originalAccess, originalRefresh);

        return second;
    }

    // ===== El camino completo =====

    [Fact]
    public async Task TheWholeFlow_ChangesThePassword_AndTheCurrentSessionKeepsWorking()
    {
        var account = await fixture.RegisterOwnerAsync();
        var next = NewSecret();

        var response = await ChangeAsync(account.AccessToken, account.Password, next);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength ?? 0);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.OK, await LoginAsync(account.Email, next));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(account.Email, account.Password)); // la vieja ya no vale

        // La sesión desde la que se cambió SIGUE valiendo, sin renovarla: ni el access token ni el refresh token.
        Assert.Equal(HttpStatusCode.OK, await MeAsync(account.AccessToken));
        Assert.Equal(HttpStatusCode.OK, await RefreshAsync(account.RefreshToken));
    }

    [Fact]
    public async Task TheOtherSessions_Die_ButTheCurrentOneStays()
    {
        var account = await fixture.RegisterOwnerAsync();
        var phone = await SecondDeviceAsync(account);
        var tablet = await SecondDeviceAsync(account);
        Assert.Equal(3, await ActiveSessionsAsync(account.AccessToken));

        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(account.AccessToken, account.Password, NewSecret())).StatusCode);

        // Primero el control: la actual, intacta, y su lista de sesiones solo la muestra a ella.
        Assert.Equal(HttpStatusCode.OK, await MeAsync(account.AccessToken));
        Assert.Equal(1, await ActiveSessionsAsync(account.AccessToken));

        // Spec 015: el access token de una sesión cerrada deja de valer en su siguiente petición.
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(phone.Access));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(tablet.Access));

        // Y su refresh token ya no sirve. VA LO ÚLTIMO a propósito: usar el refresh token de una sesión ya cerrada se detecta como reutilización (ADR-007) y revoca el resto de las sesiones de la cuenta.
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshAsync(phone.Refresh));
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshAsync(tablet.Refresh));
    }

    [Fact]
    public async Task OnlyTheOwnAccount_IsTouched_AndAUserIdOrTenantIdInTheBodyIsIgnored()
    {
        var me = await fixture.RegisterOwnerAsync();
        var other = await fixture.RegisterOwnerAsync("Empresa ajena");
        var otherSession = await SecondDeviceAsync(other);
        var next = NewSecret();

        // El cuerpo intenta apuntar a otra persona y a otra empresa: el comando no tiene esos campos, así que se ignoran.
        var response = await fixture.SendAsync(HttpMethod.Put, PasswordRoute,
            new { currentPassword = me.Password, newPassword = next, userId = UserIdOf(other.AccessToken), tenantId = Guid.NewGuid(), email = other.Email }, me.AccessToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await LoginAsync(me.Email, next));
        // La otra cuenta, intacta: su contraseña y sus sesiones.
        Assert.Equal(HttpStatusCode.OK, await LoginAsync(other.Email, other.Password));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(otherSession.Access));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(other.AccessToken));
    }

    // ===== R1: la contraseña actual, y el bloqueo =====

    [Fact]
    public async Task AWrongCurrentPassword_Is400_ChangesNothing_AndKeepsTheOtherSessions()
    {
        var account = await fixture.RegisterOwnerAsync();
        var phone = await SecondDeviceAsync(account);

        var response = await ChangeAsync(account.AccessToken, "incorrecta", NewSecret());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("password.current_invalid", await CodeOfAsync(response));
        Assert.Equal(HttpStatusCode.OK, await LoginAsync(account.Email, account.Password)); // la contraseña no cambió
        Assert.Equal(HttpStatusCode.OK, await MeAsync(phone.Access));                        // y nadie perdió su sesión
    }

    [Fact]
    public async Task TheFailuresOfTheCurrentPassword_CountForTheAccountLockout_TheFifthLocksIt()
    {
        var account = await fixture.RegisterOwnerAsync();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            Assert.Equal("password.current_invalid", await CodeOfAsync(await ChangeAsync(account.AccessToken, "incorrecta", NewSecret())));
        }

        // Bloqueada: ni la contraseña correcta entra, ni sirve para cambiarla (y no se distingue de una errónea: el mismo error).
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(account.Email, account.Password));
        var withTheRightOne = await ChangeAsync(account.AccessToken, account.Password, NewSecret());
        Assert.Equal(HttpStatusCode.BadRequest, withTheRightOne.StatusCode);
        Assert.Equal("password.current_invalid", await CodeOfAsync(withTheRightOne));
    }

    [Fact]
    public async Task ASuccessfulChange_ResetsTheCounter_SoLaterFailuresStartFromZero()
    {
        var account = await fixture.RegisterOwnerAsync();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await ChangeAsync(account.AccessToken, "incorrecta", NewSecret());
        }

        var next = NewSecret();
        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(account.AccessToken, account.Password, next)).StatusCode);

        // Con el contador a cero, cuatro fallos más NO bloquean (3 + 4 = 7 lo habría bloqueado); y la nueva entra.
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await ChangeAsync(account.AccessToken, "incorrecta", NewSecret());
        }

        Assert.Equal(HttpStatusCode.OK, await LoginAsync(account.Email, next));
    }

    // ===== R4: las longitudes, sin gastar intentos =====

    [Theory]
    [InlineData(7)]
    [InlineData(129)]
    public async Task ANewPasswordOutsideTheBounds_IsAFieldError_AndSpendsNoAttemptOfTheCurrentPassword(int length)
    {
        var account = await fixture.RegisterOwnerAsync();
        var tooShortOrLong = new string('x', length);

        // Cinco peticiones con una actual ERRÓNEA y una nueva fuera de límites: si el error de longitud llegara después de comprobar la actual, estas cinco bloquearían la cuenta.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var response = await ChangeAsync(account.AccessToken, "incorrecta", tooShortOrLong);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("validation.failed", await CodeOfAsync(response));
        }

        Assert.Equal(HttpStatusCode.OK, await LoginAsync(account.Email, account.Password)); // no se bloqueó
    }

    [Theory]
    [InlineData(8)]
    [InlineData(128)]
    public async Task TheBoundsThemselves_AreValid(int length)
    {
        var account = await fixture.RegisterOwnerAsync();
        var next = new string('y', length);

        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(account.AccessToken, account.Password, next)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await LoginAsync(account.Email, next));
    }

    [Fact]
    public async Task ANewPasswordEqualToTheCurrent_Is400Unchanged_AndNothingChanges()
    {
        var account = await fixture.RegisterOwnerAsync();
        var phone = await SecondDeviceAsync(account);

        var response = await ChangeAsync(account.AccessToken, account.Password, account.Password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("password.unchanged", await CodeOfAsync(response));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(phone.Access)); // nadie perdió su sesión: no se hizo nada
    }

    // ===== R2: el segundo factor =====

    [Fact]
    public async Task WithTheFactor_AMissingOrWrongCode_Is400_AndTheRightOneChangesIt_KeepingTheFactor()
    {
        var account = await fixture.NewMfaAccountAsync();
        var next = NewSecret();

        var missing = await ChangeAsync(account.AccessToken, account.Password, next);
        Assert.Equal("mfa.invalid_code", await CodeOfAsync(missing));
        var wrong = await ChangeAsync(account.AccessToken, account.Password, next, code: fixture.FreshCode(account) == "000000" ? "000001" : "000000");
        Assert.Equal("mfa.invalid_code", await CodeOfAsync(wrong));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(account.AccessToken));

        var changed = await ChangeAsync(account.AccessToken, account.Password, next, code: fixture.FreshCode(account));
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        // La contraseña cambió y el segundo factor SIGUE ahí: el login con la nueva pide el código.
        await AssertLoginAsksForTheCodeAsync(account.Email, next);
        var status = await MfaFlowFixture.JsonOf(await fixture.SendAsync(HttpMethod.Get, "/api/v1/account/mfa", bearer: account.AccessToken));
        Assert.True(status.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task WithTheFactor_ACodeThatWasAlreadyUsed_IsRefused_EachStepOf30SecondsIsWorthOnce()
    {
        var account = await fixture.NewMfaAccountAsync();
        var first = NewSecret();
        var code = fixture.FreshCode(account);
        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(account.AccessToken, account.Password, first, code)).StatusCode);

        // El MISMO código otra vez, con la contraseña correcta ahora (la que se acaba de poner): ese paso ya se gastó.
        var again = await ChangeAsync(account.AccessToken, first, NewSecret(), code);

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("mfa.invalid_code", await CodeOfAsync(again));
        // Control: con un código de un paso nuevo, sí.
        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(account.AccessToken, first, NewSecret(), fixture.FreshCode(account))).StatusCode);
    }

    [Fact]
    public async Task WithTheFactor_ARecoveryCodeAlsoWorks_ForWhoLostThePhone()
    {
        var account = await fixture.NewMfaAccountAsync();
        var next = NewSecret();

        var response = await ChangeAsync(account.AccessToken, account.Password, next, code: account.RecoveryCodes[0]);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AssertLoginAsksForTheCodeAsync(account.Email, next); // el factor sigue activo
    }

    [Fact]
    public async Task WithoutTheFactor_TheCodeFieldIsIgnored()
    {
        var account = await fixture.RegisterOwnerAsync();

        var response = await ChangeAsync(account.AccessToken, account.Password, NewSecret(), code: "000000");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ===== R11: quién puede =====

    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    [InlineData(TenantRole.Manager)]
    [InlineData(TenantRole.Technician)]
    [InlineData(TenantRole.Employee)]
    public async Task EveryRole_CanChangeItsOwnPassword(TenantRole role)
    {
        var owner = await fixture.RegisterOwnerAsync();
        var member = role == TenantRole.Owner ? owner : await fixture.InviteAndJoinAsync(owner, role);
        var next = NewSecret();

        var response = await ChangeAsync(member.AccessToken, member.Password, next);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await LoginAsync(member.Email, next));
    }

    [Fact]
    public async Task WithoutAToken_WithGarbage_OrWithAPlatformSession_ItIs401_AndNothingChanges()
    {
        var account = await fixture.RegisterOwnerAsync();
        var platformToken = await fixture.PlatformTokenAsync(await fixture.CreateOperatorAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, (await ChangeAsync(null, account.Password, NewSecret())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ChangeAsync("no-es-un-token", account.Password, NewSecret())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ChangeAsync(platformToken, account.Password, NewSecret())).StatusCode); // el operador no es una persona de empresa
        Assert.Equal(HttpStatusCode.OK, await LoginAsync(account.Email, account.Password)); // control: nada cambió
    }

    [Fact]
    public async Task AMemberWhoWasRemoved_CannotChangeAnythingWithTheStillValidToken()
    {
        var owner = await fixture.RegisterOwnerAsync();
        var member = await fixture.InviteAndJoinAsync(owner, TenantRole.Technician);
        await fixture.RemoveMembershipKeepingSessionsAsync(member);

        var response = await ChangeAsync(member.AccessToken, member.Password, NewSecret());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // la revalidación de la pertenencia (ADR-013)
        Assert.Equal("auth.forbidden", await CodeOfAsync(response));
    }

    // ===== Carreras =====

    [Fact]
    public async Task TwoSimultaneousChanges_LeaveExactlyOnePasswordAndNoOtherSession_NeverA500()
    {
        for (var round = 1; round <= 4; round++)
        {
            var account = await fixture.RegisterOwnerAsync();
            var phone = await SecondDeviceAsync(account);
            var (first, second) = (NewSecret(), NewSecret());

            var results = await Task.WhenAll(
                ChangeAsync(account.AccessToken, account.Password, first),
                ChangeAsync(account.AccessToken, account.Password, second));

            // Cada una recibe 204 o, si la otra confirmó antes y su «actual» ya no valía, 400: nunca un 500.
            Assert.All(results, r => Assert.True(r.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.BadRequest, $"ronda {round}: {(int)r.StatusCode}"));
            Assert.Contains(results, r => r.StatusCode == HttpStatusCode.NoContent);

            var exactlyOne = 0;
            foreach (var candidate in new[] { first, second, account.Password })
            {
                if (await LoginAsync(account.Email, candidate) == HttpStatusCode.OK)
                {
                    exactlyOne++;
                }
            }

            Assert.Equal(1, exactlyOne); // una sola contraseña vale (la vieja no)
            Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(phone.Access)); // y ninguna sesión ajena sobrevive
        }
    }

    // ===== El límite de peticiones (R12) =====

    [Fact]
    public async Task ElevenCalls_FromOneIp_AreRejectedWith429_AndTheOtherBucketsAreNotSpent()
    {
        var account = await fixture.RegisterOwnerAsync();
        var ip = TestClientIpStartupFilter.NewIp();

        HttpRequestMessage NewRequest(HttpMethod method, string path, object? body = null)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Add(TestClientIpStartupFilter.HeaderName, ip);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            return request;
        }

        // Diez con una actual errónea (la sesión no se toca, así que el token sigue valiendo para la undécima).
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var ok = await fixture.Http.SendAsync(NewRequest(HttpMethod.Put, PasswordRoute, new { currentPassword = "incorrecta", newPassword = NewSecret() }));
            Assert.Equal(HttpStatusCode.BadRequest, ok.StatusCode);
        }

        var rejected = await fixture.Http.SendAsync(NewRequest(HttpMethod.Put, PasswordRoute, new { currentPassword = "incorrecta", newPassword = NewSecret() }));

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        // La ventana es de UNA HORA: Retry-After es lo que queda de ella (si fuera de un minuto no pasaría de 60).
        Assert.InRange(int.Parse(rejected.Headers.GetValues("Retry-After").Single()), 61, 3600);

        // Cubo propio: desde la misma IP, «quién soy» y cerrar mis sesiones (otro cubo) siguen respondiendo lo suyo.
        Assert.Equal(HttpStatusCode.OK, (await fixture.Http.SendAsync(NewRequest(HttpMethod.Get, "/api/v1/account/me"))).StatusCode);
    }

    // ===== R14: los registros =====

    [Fact]
    public async Task TheEvent_IsLogged_WithTheUserAndTheClosedSessions_AndNoSecretEverReachesTheLog()
    {
        var account = await fixture.NewMfaAccountAsync();
        await SecondDeviceAsync(account);
        var userId = UserIdOf(account.AccessToken);
        var (current, next) = (account.Password, NewSecret());
        var code = fixture.FreshCode(account);
        var before = fixture.Logs.Entries.Count;

        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(account.AccessToken, current, next, code)).StatusCode);

        var entries = fixture.Logs.Entries.Skip(before).ToList();
        var entry = Assert.Single(entries, e => e.EventId == SecurityEventIds.PasswordChangedFromSession);
        Assert.Contains(userId, entry.Message);
        Assert.Contains("1", entry.Message); // una sesión ajena cerrada
        foreach (var secret in new[] { current, next, code, account.AccessToken, account.RefreshToken })
        {
            Assert.DoesNotContain(entries, e => e.Message.Contains(secret, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(entries, e => e.Message.Contains(account.Email, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AFailedChange_LeavesNoChangeEvent_NorAnySecret()
    {
        var account = await fixture.RegisterOwnerAsync();
        var (wrong, next) = (NewSecret(), NewSecret());
        var before = fixture.Logs.Entries.Count;

        await ChangeAsync(account.AccessToken, wrong, next);

        var entries = fixture.Logs.Entries.Skip(before).ToList();
        Assert.DoesNotContain(entries, e => e.EventId == SecurityEventIds.PasswordChangedFromSession);
        foreach (var secret in new[] { wrong, next, account.Password })
        {
            Assert.DoesNotContain(entries, e => e.Message.Contains(secret, StringComparison.Ordinal));
        }
    }
}

/// <summary>El aviso por correo y los enlaces de recuperación (spec 019 R7 y R8), con un transporte de correo que guarda lo enviado: el token del enlace se lee del correo que «llega».</summary>
public class ChangePasswordNoticeEndToEndTests(ResetFlowFixture fixture) : IClassFixture<ResetFlowFixture>
{
    private const string Marker = "#token=";
    private const string PasswordResetSubject = "Restablece tu contraseña de OfiFlow";

    private Task<HttpResponseMessage> ChangeAsync(string token, string current, string next) =>
        fixture.SendAsync(HttpMethod.Put, "/api/v1/account/password", new { currentPassword = current, newPassword = next }, token);

    private async Task<EmailMessage?> WaitForAsync(string email, string subject, int skip = 0, int seconds = 10)
    {
        var limit = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < limit)
        {
            var found = fixture.Email.Sent.Where(m => m.To.Value == email && m.Subject == subject).Skip(skip).FirstOrDefault();
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(25);
        }

        return null;
    }

    private async Task<string> RequestRecoveryLinkAsync(string email)
    {
        var already = fixture.Email.Sent.Count(m => m.To.Value == email && m.Subject == PasswordResetSubject);
        Assert.Equal(HttpStatusCode.Accepted, (await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/forgot-password", new { email })).StatusCode);
        var message = await WaitForAsync(email, PasswordResetSubject, skip: already);
        Assert.NotNull(message);
        var link = message.TextBody.Split("\r\n").Single(line => line.StartsWith("https://", StringComparison.Ordinal));

        return link[(link.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length)..];
    }

    private Task<HttpResponseMessage> ResetAsync(string token, string password) =>
        fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/reset-password", new { token, newPassword = password });

    [Fact]
    public async Task AFixedNotice_IsSentOnce_ToTheAccountEmail_WithoutAnyPassword()
    {
        var account = await fixture.RegisterOwnerAsync();
        var (current, next) = (account.Password, MembersFixture.NewPassword());

        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(account.AccessToken, current, next)).StatusCode);

        var notice = await WaitForAsync(account.Email, OfiFlow.Application.Identity.PasswordChangedEmail.Subject);
        Assert.NotNull(notice);
        Assert.DoesNotContain(current, notice.Subject + notice.TextBody);
        Assert.DoesNotContain(next, notice.Subject + notice.TextBody);
        await Task.Delay(200);
        Assert.Single(fixture.Email.Sent, m => m.To.Value == account.Email && m.Subject == OfiFlow.Application.Identity.PasswordChangedEmail.Subject);
    }

    [Fact]
    public async Task AFailedChange_SendsNoNotice()
    {
        var account = await fixture.RegisterOwnerAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await ChangeAsync(account.AccessToken, "incorrecta", MembersFixture.NewPassword())).StatusCode);

        await Task.Delay(300);
        Assert.DoesNotContain(fixture.Email.Sent, m => m.To.Value == account.Email && m.Subject == OfiFlow.Application.Identity.PasswordChangedEmail.Subject);
    }

    [Fact]
    public async Task APendingRecoveryLink_StopsWorking_WhenThePersonChangesThePasswordFromInside()
    {
        var account = await fixture.RegisterOwnerAsync();
        var other = await fixture.RegisterOwnerAsync("Empresa ajena");
        var link = await RequestRecoveryLinkAsync(account.Email);
        var otherLink = await RequestRecoveryLinkAsync(other.Email);

        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(account.AccessToken, account.Password, MembersFixture.NewPassword())).StatusCode);

        // El enlace pedido ANTES (por quien fuera) ya no sirve: el mismo error que cualquier enlace que no vale.
        var voided = await ResetAsync(link, MembersFixture.NewPassword());
        Assert.Equal(HttpStatusCode.BadRequest, voided.StatusCode);
        Assert.Equal("password_reset.invalid", JsonDocument.Parse(await voided.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        // Control: el enlace de OTRA cuenta no se tocó.
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(otherLink, MembersFixture.NewPassword())).StatusCode);
    }
}

/// <summary>La puerta del segundo factor obligatorio (spec 022) con la regla REAL: cambiar la contraseña no está en la lista cerrada de cuatro operaciones permitidas sin el factor (spec 019 R10).</summary>
public class ChangePasswordSecondFactorGateTests(SecondFactorGateFixture fixture) : IClassFixture<SecondFactorGateFixture>
{
    private Task<HttpResponseMessage> ChangeAsync(string token, string current, string next, string? code = null) =>
        fixture.SendAsync(HttpMethod.Put, "/api/v1/account/password", code is null ? new { currentPassword = current, newPassword = next } : new { currentPassword = current, newPassword = next, code }, token);

    [Fact]
    public async Task AnOwnerWithoutTheFactor_Gets403EnrollmentRequired_AndTheirPasswordIsUntouched()
    {
        var owner = await fixture.RegisterOwnerAsync();

        var response = await ChangeAsync(owner.AccessToken, owner.Password, MembersFixture.NewPassword());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("mfa.enrollment_required", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = owner.Email, password = owner.Password })).StatusCode);
    }

    [Fact]
    public async Task TheControl_AnOwnerWithTheFactor_CanChangeItWithTheCode()
    {
        var owner = await fixture.NewMfaAccountAsync();

        var response = await ChangeAsync(owner.AccessToken, owner.Password, MembersFixture.NewPassword(), fixture.FreshCode(owner));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
