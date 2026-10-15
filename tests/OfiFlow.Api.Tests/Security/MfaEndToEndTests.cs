using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Identity.Mfa;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// El segundo factor de principio a fin por HTTP, con la API entera, SQL Server real y códigos TOTP de verdad (spec 010,
/// bloque 7). Cada denegación va con su control positivo: la misma preparación con el resultado bueno, para que un "falla" no
/// se deba a un error de la prueba. El reloj lo controla el test (ver <see cref="MfaFlowFixture"/>).
/// </summary>
public class MfaEndToEndTests(MfaFlowFixture fixture) : IClassFixture<MfaFlowFixture>
{
    private const string TooShort = "12345";

    private HttpClient Http => fixture.Http;

    private static string WrongCodeFor(string rightCode) => rightCode == "000000" ? "000001" : "000000";

    // ===== El ciclo completo =====

    [Fact]
    public async Task TheCompleteCycle_Register_Enable_TwoStepLogin_RecoveryCode_Disable()
    {
        var account = await fixture.RegisterOwnerAsync();
        var firstRefreshToken = account.RefreshToken;

        // 1. Activar: pedir el secreto y confirmarlo con el primer código de la app.
        var setup = await fixture.SetupAsync(account);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        Assert.Equal("no-store", setup.Headers.CacheControl?.ToString());

        var enable = await fixture.SendAsync(HttpMethod.Post, "/api/v1/account/mfa/enable", new { code = fixture.CodeNow(account) }, account.AccessToken);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.Equal("no-store", enable.Headers.CacheControl?.ToString());
        var recoveryCodes = (await MfaFlowFixture.JsonOf(enable)).GetProperty("codes").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Equal(10, recoveryCodes.Count);
        Assert.All(recoveryCodes, code => Assert.Matches(MfaFlowFixture.RecoveryCodePattern, code));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        // 2. Activar cierra las sesiones: la renovación anterior ya no sirve; el token de acceso ya emitido sigue valiendo (límite conocido, 30 min).
        var oldRefresh = await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = firstRefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, oldRefresh.StatusCode);
        var oldAccess = await fixture.SendAsync(HttpMethod.Get, "/api/v1/account/mfa", bearer: account.AccessToken);
        Assert.Equal(HttpStatusCode.OK, oldAccess.StatusCode);

        // 3. Login en dos pasos: la contraseña da un desafío, NO una sesión.
        var loginResponse = await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = account.Email, password = account.Password });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        Assert.Equal("no-store", loginResponse.Headers.CacheControl?.ToString()); // el desafío es un secreto que no se vuelve a mostrar
        var login = await MfaFlowFixture.JsonOf(loginResponse);
        Assert.True(login.GetProperty("mfaRequired").GetBoolean());
        Assert.Equal(43, login.GetProperty("challenge").GetString()!.Length);
        Assert.False(login.TryGetProperty("accessToken", out _));
        Assert.False(login.TryGetProperty("refreshToken", out _));

        // 4. Canjear el desafío con un código de la app da la sesión, y esa sesión sirve de verdad.
        var verify = await fixture.VerifyAsync(login.GetProperty("challenge").GetString()!, fixture.FreshCode(account));
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var tokens = await MfaFlowFixture.JsonOf(verify);
        account.AccessToken = tokens.GetProperty("accessToken").GetString()!;
        account.RefreshToken = tokens.GetProperty("refreshToken").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: account.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = account.RefreshToken })).StatusCode);

        // 5. Con un código de recuperación (se perdió el móvil): entra una vez, y quedan 9.
        var recoveryChallenge = await fixture.LoginForChallengeAsync(account);
        var withRecovery = await fixture.VerifyAsync(recoveryChallenge, recoveryCodes[0]);
        Assert.Equal(HttpStatusCode.OK, withRecovery.StatusCode);
        account.AccessToken = (await MfaFlowFixture.JsonOf(withRecovery)).GetProperty("accessToken").GetString()!;
        var status = await MfaFlowFixture.JsonOf(await fixture.SendAsync(HttpMethod.Get, "/api/v1/account/mfa", bearer: account.AccessToken));
        Assert.Equal(9, status.GetProperty("recoveryCodesRemaining").GetInt32());

        // 6. Desactivar: contraseña y código. Vuelve el login de un solo paso, y las sesiones se cierran otra vez.
        var disable = await fixture.SendAsync(HttpMethod.Post, "/api/v1/account/mfa/disable",
            new { password = account.Password, code = fixture.FreshCode(account) }, account.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = account.RefreshToken })).StatusCode);

        var plainLogin = await MfaFlowFixture.JsonOf(await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = account.Email, password = account.Password }));
        Assert.True(plainLogin.TryGetProperty("accessToken", out _));
        Assert.False(plainLogin.TryGetProperty("mfaRequired", out _));
    }

    // ===== Activar =====

    [Fact]
    public async Task EnablingTwice_AndAskingForANewSecretWhileEnabled_AreRefused()
    {
        var account = await fixture.NewMfaAccountAsync();

        var setupAgain = await fixture.SendAsync(HttpMethod.Post, "/api/v1/account/mfa/setup", new { password = account.Password }, account.AccessToken);
        var enableAgain = await fixture.SendAsync(HttpMethod.Post, "/api/v1/account/mfa/enable", new { code = fixture.FreshCode(account) }, account.AccessToken);

        Assert.Equal(HttpStatusCode.BadRequest, setupAgain.StatusCode);
        Assert.Equal("mfa.already_enabled", (await MfaFlowFixture.JsonOf(setupAgain)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, enableAgain.StatusCode);
        Assert.Equal("mfa.already_enabled", (await MfaFlowFixture.JsonOf(enableAgain)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ASecretThatWasNeverConfirmed_DoesNotChangeTheLogin()
    {
        // Hasta confirmar el primer código, el MFA no cuenta: quien se equivocó al escanear sigue pudiendo entrar.
        var account = await fixture.RegisterOwnerAsync();
        Assert.Equal(HttpStatusCode.OK, (await fixture.SetupAsync(account)).StatusCode);

        var login = await MfaFlowFixture.JsonOf(await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = account.Email, password = account.Password }));

        Assert.True(login.TryGetProperty("accessToken", out _));
        Assert.False(login.TryGetProperty("mfaRequired", out _));
    }

    [Fact]
    public async Task TwoSimultaneousConfirmations_WithTheSameCode_ActivateItExactlyOnce()
    {
        var account = await fixture.RegisterOwnerAsync();
        Assert.Equal(HttpStatusCode.OK, (await fixture.SetupAsync(account)).StatusCode);
        var code = fixture.CodeNow(account);

        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
            fixture.SendAsync(HttpMethod.Post, "/api/v1/account/mfa/enable", new { code }, account.AccessToken))));

        // Una gana; las demás reciben un 400 con código (nunca un 500: esa carrera es la del catch de DbUpdateException).
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(response => response.StatusCode != HttpStatusCode.OK), response =>
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
    }

    [Fact]
    public async Task WrongPasswordsWhenAskingForTheSecret_CountForTheLockout_AndLockTheAccount()
    {
        var account = await fixture.RegisterOwnerAsync();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var response = await fixture.SendAsync(HttpMethod.Post, "/api/v1/account/mfa/setup", new { password = "incorrecta" }, account.AccessToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // Un token robado no puede probar contraseñas por esta vía sin límite: a los 5 fallos la cuenta queda bloqueada.
        var login = await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = account.Email, password = account.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        // Pasados los 15 minutos, la cuenta vuelve a funcionar (control positivo).
        fixture.Clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(HttpStatusCode.OK, (await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = account.Email, password = account.Password })).StatusCode);
    }

    // ===== El login en dos pasos: ninguna sesión sin el segundo factor =====

    [Fact]
    public async Task TheChallenge_IsNotAnAccessToken_OnAnyAuthenticatedRoute()
    {
        var account = await fixture.NewMfaAccountAsync();
        var challenge = await fixture.LoginForChallengeAsync(account);

        string[] routes = ["/api/v1/customers", "/api/v1/jobs", "/api/v1/users", "/api/v1/invitations", "/api/v1/account/mfa"];

        foreach (var route in routes)
        {
            // Presentado como si fuera un token de acceso, el desafío no abre ninguna ruta...
            Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.SendAsync(HttpMethod.Get, route, bearer: challenge)).StatusCode);
            // ...y el token de acceso de verdad sí (control positivo).
            Assert.Equal(HttpStatusCode.OK, (await fixture.SendAsync(HttpMethod.Get, route, bearer: account.AccessToken)).StatusCode);
        }
    }

    [Fact]
    public async Task ThePasswordAloneNeverGivesASession_AndTheRefreshEndpointCannotBeUsedToGetOne()
    {
        var account = await fixture.RegisterOwnerAsync();
        await fixture.EnableMfaAsync(account); // cierra las sesiones

        var login = await MfaFlowFixture.JsonOf(await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = account.Email, password = account.Password }));

        Assert.False(login.TryGetProperty("accessToken", out _));
        Assert.False(login.TryGetProperty("refreshToken", out _));
        // Sin sesión no hay refresh token que renovar: el único camino a la sesión es el segundo paso.
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = account.RefreshToken })).StatusCode);
    }

    [Fact]
    public async Task ALoginWithAWrongPassword_GivesNoChallenge()
    {
        var account = await fixture.NewMfaAccountAsync();

        var response = await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = account.Email, password = "incorrecta" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ===== Ataques sobre el segundo paso =====

    [Fact]
    public async Task ABruteForceOfTheCode_EndsInTheLockout_AndEvenTheCorrectCodeThenFails()
    {
        var account = await fixture.NewMfaAccountAsync();
        var challenge = await fixture.LoginForChallengeAsync(account);
        var right = fixture.FreshCode(account);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.VerifyAsync(challenge, WrongCodeFor(right))).StatusCode);
        }

        // El código bueno, con el desafío bueno, ya no entra: el desafío se agotó y la cuenta está bloqueada.
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.VerifyAsync(challenge, right)).StatusCode);
        // Y la contraseña correcta tampoco abre un desafío nuevo.
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = account.Email, password = account.Password })).StatusCode);
    }

    [Fact]
    public async Task ANormalMistake_ADifferentCodeOnce_DoesNotKillTheChallenge()
    {
        var account = await fixture.NewMfaAccountAsync();
        var challenge = await fixture.LoginForChallengeAsync(account);
        var right = fixture.FreshCode(account);

        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.VerifyAsync(challenge, WrongCodeFor(right))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await fixture.VerifyAsync(challenge, right)).StatusCode); // control: un fallo no mata el desafío
    }

    [Fact]
    public async Task ATotpCodeThatWasAlreadyUsed_CannotBeUsedAgain_EvenOnANewChallenge()
    {
        var account = await fixture.NewMfaAccountAsync();
        var code = fixture.FreshCode(account);

        Assert.Equal(HttpStatusCode.OK, (await fixture.VerifyAsync(await fixture.LoginForChallengeAsync(account), code)).StatusCode);

        // El mismo código, dentro de su ventana, con un desafío nuevo: quien lo viera por encima del hombro no entra.
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.VerifyAsync(await fixture.LoginForChallengeAsync(account), code)).StatusCode);

        // Control positivo: con un código del paso siguiente, sí.
        Assert.Equal(HttpStatusCode.OK, (await fixture.VerifyAsync(await fixture.LoginForChallengeAsync(account), fixture.FreshCode(account))).StatusCode);
    }

    [Fact]
    public async Task AChallengeThatWasAlreadyRedeemed_CannotBeRedeemedAgain_EvenWithAFreshValidCode()
    {
        var account = await fixture.NewMfaAccountAsync();
        var challenge = await fixture.LoginForChallengeAsync(account);

        Assert.Equal(HttpStatusCode.OK, (await fixture.VerifyAsync(challenge, fixture.FreshCode(account))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.VerifyAsync(challenge, fixture.FreshCode(account))).StatusCode);
    }

    [Fact]
    public async Task AChallengeExpiresAfterFiveMinutes()
    {
        var account = await fixture.NewMfaAccountAsync();
        var challenge = await fixture.LoginForChallengeAsync(account);

        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.VerifyAsync(challenge, fixture.FreshCode(account))).StatusCode);

        // Control positivo: un desafío nuevo funciona.
        Assert.Equal(HttpStatusCode.OK, (await fixture.VerifyAsync(await fixture.LoginForChallengeAsync(account), fixture.FreshCode(account))).StatusCode);
    }

    [Fact]
    public async Task AChallengeOfOneAccount_CannotBeRedeemedWithTheCodeOfAnother()
    {
        var ana = await fixture.NewMfaAccountAsync();
        var luis = await fixture.NewMfaAccountAsync();
        var anasChallenge = await fixture.LoginForChallengeAsync(ana);

        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.VerifyAsync(anasChallenge, fixture.FreshCode(luis))).StatusCode);

        // Control positivo: el desafío de Ana sigue vivo para Ana, con su propio código.
        Assert.Equal(HttpStatusCode.OK, (await fixture.VerifyAsync(anasChallenge, fixture.FreshCode(ana))).StatusCode);
    }

    [Fact]
    public async Task EveryFailureOfTheSecondStep_LooksExactlyTheSame()
    {
        var account = await fixture.NewMfaAccountAsync();
        var usedChallenge = await fixture.LoginForChallengeAsync(account);
        Assert.Equal(HttpStatusCode.OK, (await fixture.VerifyAsync(usedChallenge, fixture.FreshCode(account))).StatusCode);

        var expiredChallenge = await fixture.LoginForChallengeAsync(account);
        var liveChallenge = await fixture.LoginForChallengeAsync(account);
        fixture.Clock.Advance(TimeSpan.FromMinutes(6)); // el desafío de arriba caduca; para el vivo se pide otro
        liveChallenge = await fixture.LoginForChallengeAsync(account);
        var right = fixture.FreshCode(account);

        var fingerprints = new List<string>
        {
            await MfaFlowFixture.FingerprintAsync(await fixture.VerifyAsync(new string('A', 43), right)), // desconocido
            await MfaFlowFixture.FingerprintAsync(await fixture.VerifyAsync(usedChallenge, right)), // ya usado
            await MfaFlowFixture.FingerprintAsync(await fixture.VerifyAsync(expiredChallenge, right)), // caducado
            await MfaFlowFixture.FingerprintAsync(await fixture.VerifyAsync(liveChallenge, WrongCodeFor(right))), // código erróneo
            await MfaFlowFixture.FingerprintAsync(await fixture.VerifyAsync("no-es-un-desafio", right)), // forma equivocada
            await MfaFlowFixture.FingerprintAsync(await fixture.VerifyAsync(liveChallenge, "no es un código")) // código mal escrito
        };

        Assert.StartsWith("401|", fingerprints[0]);
        Assert.Single(fingerprints.Distinct()); // quien lo intenta no aprende cuál de los seis fue
    }

    [Fact]
    public async Task FourSimultaneousRedemptions_OfTheSameChallengeAndCode_GiveExactlyOneSession()
    {
        var account = await fixture.NewMfaAccountAsync();
        var challenge = await fixture.LoginForChallengeAsync(account);
        var code = fixture.FreshCode(account);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => fixture.VerifyAsync(challenge, code))));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(response => response.StatusCode != HttpStatusCode.OK), response =>
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
    }

    // ===== La contraseña no reinicia el contador (R17) =====

    [Fact]
    public async Task ThePasswordInterleavedBetweenFailures_DoesNotResetTheCounter_SoTheFifthFailureLocks()
    {
        var account = await fixture.NewMfaAccountAsync();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = account.Email, password = "incorrecta" })).StatusCode);
        }

        // La contraseña correcta intercalada (el ataque de quien ya la conoce) da un desafío, pero NO reinicia el contador...
        var challenge = await fixture.LoginForChallengeAsync(account);

        // ...así que un único fallo más —el quinto— bloquea la cuenta, con el desafío vivo y el código bueno.
        var right = fixture.FreshCode(account);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.VerifyAsync(challenge, WrongCodeFor(right))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.VerifyAsync(challenge, right)).StatusCode);
    }

    [Fact]
    public async Task ACompleteLogin_ResetsTheCounter()
    {
        var account = await fixture.NewMfaAccountAsync();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = account.Email, password = "incorrecta" });
        }

        await fixture.LoginWithMfaAsync(account); // contraseña y segundo factor: un acceso completo

        // Sin el reinicio, estos 4 fallos sumarían 8 y la cuenta ya estaría bloqueada; con él, aún entra.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = account.Email, password = "incorrecta" });
        }

        Assert.Equal(HttpStatusCode.OK, (await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = account.Email, password = account.Password })).StatusCode);
    }

    // ===== Desactivar y regenerar =====

    [Fact]
    public async Task Disabling_NeedsThePasswordAndAValidCode_AndEachWrongOneChangesNothing()
    {
        var account = await fixture.NewMfaAccountAsync();

        var wrongPassword = await fixture.SendAsync(HttpMethod.Post, "/api/v1/account/mfa/disable", new { password = "incorrecta", code = fixture.FreshCode(account) }, account.AccessToken);
        var wrongCode = await fixture.SendAsync(HttpMethod.Post, "/api/v1/account/mfa/disable", new { password = account.Password, code = WrongCodeFor(fixture.FreshCode(account)) }, account.AccessToken);

        Assert.Equal("mfa.invalid_password", (await MfaFlowFixture.JsonOf(wrongPassword)).GetProperty("code").GetString());
        Assert.Equal("mfa.invalid_code", (await MfaFlowFixture.JsonOf(wrongCode)).GetProperty("code").GetString());

        // El MFA sigue activo (el login sigue pidiendo el segundo paso)...
        var login = await MfaFlowFixture.JsonOf(await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = account.Email, password = account.Password }));
        Assert.True(login.GetProperty("mfaRequired").GetBoolean());

        // ...y con la contraseña y un código válidos se desactiva (control positivo).
        var ok = await fixture.SendAsync(HttpMethod.Post, "/api/v1/account/mfa/disable", new { password = account.Password, code = fixture.FreshCode(account) }, account.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
    }

    [Fact]
    public async Task Regenerating_InvalidatesTheOldRecoveryCodes_AndTheNewOnesWork()
    {
        var account = await fixture.NewMfaAccountAsync();
        var oldCodes = account.RecoveryCodes.ToList();

        var regenerate = await fixture.SendAsync(HttpMethod.Post, "/api/v1/account/mfa/recovery-codes",
            new { password = account.Password, code = fixture.FreshCode(account) }, account.AccessToken);
        Assert.Equal(HttpStatusCode.OK, regenerate.StatusCode);
        Assert.Equal("no-store", regenerate.Headers.CacheControl?.ToString());
        var newCodes = (await MfaFlowFixture.JsonOf(regenerate)).GetProperty("codes").EnumerateArray().Select(c => c.GetString()!).ToList();

        Assert.Equal(10, newCodes.Count);
        Assert.Empty(newCodes.Intersect(oldCodes));
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.VerifyAsync(await fixture.LoginForChallengeAsync(account), oldCodes[0])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.VerifyAsync(await fixture.LoginForChallengeAsync(account), newCodes[0])).StatusCode);
    }

    [Fact]
    public async Task ARecoveryCode_WorksOnce_AndNotTwice()
    {
        var account = await fixture.NewMfaAccountAsync();
        var code = account.RecoveryCodes[0];

        Assert.Equal(HttpStatusCode.OK, (await fixture.VerifyAsync(await fixture.LoginForChallengeAsync(account), code)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.VerifyAsync(await fixture.LoginForChallengeAsync(account), code)).StatusCode);
    }

    // ===== Reiniciar el segundo factor de un miembro =====

    [Fact]
    public async Task AManagerResettingAMembersMfa_RemovesTheFactorAndClosesTheirSessions()
    {
        var owner = await fixture.RegisterOwnerAsync("Empresa del reinicio");
        var member = await fixture.InviteAndJoinAsync(owner, TenantRole.Technician);
        await fixture.EnableMfaAsync(member);
        await fixture.LoginWithMfaAsync(member); // el miembro tiene ahora una sesión abierta con el MFA
        var memberRefresh = member.RefreshToken;
        var membershipId = await fixture.TenantUserIdOfAsync(owner, member);

        var reset = await fixture.SendAsync(HttpMethod.Delete, $"/api/v1/users/{membershipId}/mfa", bearer: owner.AccessToken);

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        // Sus sesiones se cierran, y su login vuelve a ser de un solo paso.
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = memberRefresh })).StatusCode);
        var login = await MfaFlowFixture.JsonOf(await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = member.Email, password = member.Password }));
        Assert.True(login.TryGetProperty("accessToken", out _));
        Assert.False(login.TryGetProperty("mfaRequired", out _));
    }

    [Fact]
    public async Task AnAdmin_CannotResetTheOwnersMfa_AndTheOwnersFactorStaysIntact()
    {
        var owner = await fixture.RegisterOwnerAsync("Empresa del dueño protegido");
        await fixture.EnableMfaAsync(owner);
        await fixture.LoginWithMfaAsync(owner);
        // El Owner invita a un Admin (el Owner ya tiene MFA: su sesión viene del segundo paso).
        var admin = await fixture.InviteAndJoinAsync(owner, TenantRole.Admin);
        var ownerMembershipId = await fixture.TenantUserIdOfAsync(owner, new MfaFlowFixture.Account(owner.Email, owner.Password));

        var reset = await fixture.SendAsync(HttpMethod.Delete, $"/api/v1/users/{ownerMembershipId}/mfa", bearer: admin.AccessToken);

        Assert.Equal(HttpStatusCode.Forbidden, reset.StatusCode);
        Assert.Equal("tenant_user.role_not_manageable", (await MfaFlowFixture.JsonOf(reset)).GetProperty("code").GetString());

        // El factor del Owner sigue ahí: su login sigue pidiendo el segundo paso.
        var login = await MfaFlowFixture.JsonOf(await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = owner.Email, password = owner.Password }));
        Assert.True(login.GetProperty("mfaRequired").GetBoolean());
    }

    // ===== Lo que no debe quedar en ningún sitio =====

    [Fact]
    public async Task NeitherTheSecretNorTheCodesNorTheChallenges_AreInTheDatabaseInClear()
    {
        var account = await fixture.RegisterOwnerAsync();
        await fixture.EnableMfaAsync(account);
        var challenge = await fixture.LoginForChallengeAsync(account);
        var usedCode = fixture.FreshCode(account);
        await fixture.VerifyAsync(challenge, usedCode);
        var secretInBase32 = Convert.ToHexString(account.Secret);

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var stored = new List<string>();
        stored.AddRange(await db.Database.SqlQueryRaw<string>("SELECT SecretCipher AS Value FROM MfaCredentials").ToListAsync());
        stored.AddRange(await db.Database.SqlQueryRaw<string>("SELECT CodeHash AS Value FROM RecoveryCodes").ToListAsync());
        stored.AddRange(await db.Database.SqlQueryRaw<string>("SELECT TokenHash AS Value FROM MfaChallenges").ToListAsync());
        Assert.NotEmpty(stored);

        Assert.All(stored, value =>
        {
            Assert.DoesNotContain(Convert.ToBase64String(account.Secret), value);
            Assert.DoesNotContain(secretInBase32, value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(challenge, value);
            Assert.All(account.RecoveryCodes, code => Assert.DoesNotContain(code.Replace("-", ""), value.Replace("-", ""), StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task TheSecurityEvents_AreLogged_AndNeverCarrySecretsCodesChallengesEmailsOrPasswords()
    {
        var account = await fixture.RegisterOwnerAsync("Empresa de los eventos");
        await fixture.EnableMfaAsync(account);
        var challenge = await fixture.LoginForChallengeAsync(account);
        var right = fixture.FreshCode(account);
        await fixture.VerifyAsync(challenge, WrongCodeFor(right));
        await fixture.VerifyAsync(challenge, account.RecoveryCodes[0]);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = account.Email, password = "incorrecta" });
        }

        var owner = await fixture.RegisterOwnerAsync("Empresa del reinicio de eventos");
        var member = await fixture.InviteAndJoinAsync(owner, TenantRole.Employee);
        await fixture.EnableMfaAsync(member);
        await fixture.SendAsync(HttpMethod.Delete, $"/api/v1/users/{await fixture.TenantUserIdOfAsync(owner, member)}/mfa", bearer: owner.AccessToken);

        var events = fixture.Logs.Entries.Where(e => e.EventId is >= 1001 and <= 1006 or >= 1501 and <= 1507).ToList();
        var ids = events.Select(e => e.EventId).ToHashSet();
        foreach (var expected in new[] { SecurityEventIds.MfaSetupStarted, SecurityEventIds.MfaEnabled, SecurityEventIds.MfaVerificationFailed,
                     SecurityEventIds.RecoveryCodeUsed, SecurityEventIds.AccountLockedOut, SecurityEventIds.MfaResetByManager })
        {
            Assert.Contains(expected, ids);
        }

        var secrets = new List<string> { account.Email, account.Password, member.Email, member.Password, challenge, right,
            Convert.ToBase64String(account.Secret), Convert.ToHexString(account.Secret) };
        secrets.AddRange(account.RecoveryCodes);
        secrets.AddRange(account.RecoveryCodes.Select(code => code.Replace("-", "")));

        Assert.All(events, entry => Assert.All(secrets, secret =>
            Assert.DoesNotContain(secret, entry.Message, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(events, entry => Regex.IsMatch(entry.Message, @"[\w.-]+@[\w.-]+\.\w+")); // ni ningún email
    }
}
