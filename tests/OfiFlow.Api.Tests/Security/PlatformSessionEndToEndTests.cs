using System.IdentityModel.Tokens.Jwt;
using System.Net;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Identity;
using OfiFlow.Infrastructure.Identity;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// La sesión de plataforma de punta a punta (spec 011, bloque 5), con la API entera y SQL Server real. Lo que decide es lo que ve
/// quien llama: el ciclo completo, que los dos mundos no se confunden (esquemas, claves, audiencias, desafíos) y que un fallo es
/// siempre el mismo 401.
/// </summary>
public sealed class PlatformSessionEndToEndTests(PlatformFlowFixture fixture) : IClassFixture<PlatformFlowFixture>
{
    private readonly PlatformFlowFixture _fixture = fixture;

    // ===== El ciclo completo =====

    [Fact]
    public async Task TheLogin_GivesAChallengeAndNeverASession()
    {
        var platformOperator = await _fixture.CreateOperatorAsync();

        var response = await _fixture.PlatformLoginAsync(platformOperator.Email, platformOperator.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());

        var body = await PlatformFlowFixture.JsonOf(response);
        Assert.True(body.GetProperty("mfaRequired").GetBoolean());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("challenge").GetString()));
        Assert.False(body.TryGetProperty("accessToken", out _));
        Assert.False(body.TryGetProperty("refreshToken", out _));
    }

    [Fact]
    public async Task TheCompleteCycle_GivesASessionThatTheProbeRouteAccepts()
    {
        var platformOperator = await _fixture.CreateOperatorAsync();

        var session = await _fixture.OpenPlatformSessionAsync(platformOperator);
        var token = session.GetProperty("accessToken").GetString()!;

        var probe = await _fixture.ProbeAsync(token);

        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
    }

    [Fact]
    public async Task TheSession_IsFifteenMinutes_HasNoRefreshToken_AndOnlyCarriesTheAccountAndTheRealm()
    {
        var platformOperator = await _fixture.CreateOperatorAsync();

        var session = await _fixture.OpenPlatformSessionAsync(platformOperator);

        Assert.False(session.TryGetProperty("refreshToken", out _));

        var token = new JwtSecurityTokenHandler().ReadJwtToken(session.GetProperty("accessToken").GetString());
        Assert.Equal(_fixture.Jwt.PlatformAudience, Assert.Single(token.Audiences));
        Assert.Equal(platformOperator.UserId.ToString(), token.Subject);
        Assert.Equal(OfiFlowClaimTypes.PlatformRealm, token.Claims.Single(claim => claim.Type == OfiFlowClaimTypes.Realm).Value);
        Assert.Equal(PlatformAuthentication.SessionLifetime, token.ValidTo - token.ValidFrom);

        // Un operador no tiene empresa ni rol: ningún claim de empresa en su sesión.
        Assert.DoesNotContain(token.Claims, claim => claim.Type is "tenant_id" or "role");
    }

    [Fact]
    public async Task ARecoveryCode_AlsoGivesTheSession_ButOnlyOnce()
    {
        var platformOperator = await _fixture.CreateOperatorAsync();
        var recovery = platformOperator.RecoveryCodes[0];

        var first = await _fixture.PlatformVerifyAsync(await _fixture.PlatformChallengeAsync(platformOperator), recovery);
        var second = await _fixture.PlatformVerifyAsync(await _fixture.PlatformChallengeAsync(platformOperator), recovery);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task AChallenge_IsSpentByARedemption()
    {
        var platformOperator = await _fixture.CreateOperatorAsync();
        var challenge = await _fixture.PlatformChallengeAsync(platformOperator);

        Assert.Equal(HttpStatusCode.OK, (await _fixture.PlatformVerifyAsync(challenge, _fixture.FreshCode(platformOperator))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.PlatformVerifyAsync(challenge, _fixture.FreshCode(platformOperator))).StatusCode);
    }

    // ===== Sin sesión de plataforma no se entra =====

    [Fact]
    public async Task TheProbeRoute_RejectsRequestsWithoutAToken_OrWithGarbage()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.ProbeAsync(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.ProbeAsync("esto-no-es-un-token")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.ProbeAsync(string.Empty)).StatusCode);
    }

    // ===== Los dos mundos no se confunden =====

    [Fact]
    public async Task AForgedPlatformTokenWithTheRightKeyAndAudience_IsAccepted_ControlForTheNextTests()
    {
        // Control positivo: sin él, los rechazos de abajo podrían ser de cualquier otra causa (una fábrica de tokens rota).
        var platformOperator = await _fixture.CreateOperatorAsync();

        var response = await _fixture.ProbeAsync(_fixture.ForgePlatform(platformOperator.UserId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ACompanySession_IsRejectedOnAPlatformRoute()
    {
        var owner = await _fixture.NewMfaAccountAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.ProbeAsync(owner.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task APlatformSession_IsRejectedOnEveryCompanyRoute()
    {
        var platformOperator = await _fixture.CreateOperatorAsync();
        var token = await _fixture.PlatformTokenAsync(platformOperator);

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, "/api/v1/customers"),
                     (HttpMethod.Get, "/api/v1/jobs"),
                     (HttpMethod.Get, "/api/v1/users"),
                     (HttpMethod.Post, "/api/v1/account/mfa/setup")
                 })
        {
            var response = await _fixture.SendAsync(method, path, bearer: token);

            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path} respondió {(int)response.StatusCode} a una sesión de plataforma.");
        }
    }

    [Fact]
    public async Task ATokenSignedWithTheCompanyKey_ButWithThePlatformAudience_IsRejected()
    {
        // El ataque que la clave propia evita: si se pudiera firmar con la clave de las empresas y poner la audiencia de la plataforma.
        var platformOperator = await _fixture.CreateOperatorAsync();

        var token = _fixture.Forge(_fixture.Database.JwtSecret, _fixture.Jwt.PlatformAudience, platformOperator.UserId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.ProbeAsync(token)).StatusCode);
    }

    [Fact]
    public async Task ATokenSignedWithThePlatformKey_ButWithTheCompanyAudience_IsRejected()
    {
        var platformOperator = await _fixture.CreateOperatorAsync();

        var token = _fixture.Forge(_fixture.Database.PlatformSecret, _fixture.Database.JwtAudience, platformOperator.UserId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.ProbeAsync(token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: token)).StatusCode);
    }

    [Fact]
    public async Task ACompanyTokenCarryingThePlatformRealmClaim_IsRejectedOnAPlatformRoute()
    {
        // Poner el claim del mundo no basta: el token tiene que haber pasado la validación del esquema de plataforma.
        var platformOperator = await _fixture.CreateOperatorAsync();

        var token = _fixture.Forge(_fixture.Database.JwtSecret, _fixture.Database.JwtAudience, platformOperator.UserId, platformRealm: true);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.ProbeAsync(token)).StatusCode);
    }

    [Fact]
    public async Task APlatformTokenWithoutTheRealmClaim_IsRefusedByThePolicy()
    {
        // Bien firmado pero sin el claim del mundo: autenticado, no autorizado.
        var platformOperator = await _fixture.CreateOperatorAsync();

        var token = _fixture.ForgePlatform(platformOperator.UserId, platformRealm: false);

        Assert.Equal(HttpStatusCode.Forbidden, (await _fixture.ProbeAsync(token)).StatusCode);
    }

    [Fact]
    public async Task AnExpiredPlatformToken_IsRejected()
    {
        var platformOperator = await _fixture.CreateOperatorAsync();

        var token = _fixture.ForgePlatform(platformOperator.UserId, lifetime: TimeSpan.FromMinutes(-1));

        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.ProbeAsync(token)).StatusCode);
    }

    [Fact]
    public async Task ATokenSignedWithAnyOtherKey_IsRejected()
    {
        var platformOperator = await _fixture.CreateOperatorAsync();

        var token = _fixture.Forge(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), _fixture.Jwt.PlatformAudience, platformOperator.UserId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.ProbeAsync(token)).StatusCode);
    }

    // ===== R10: la base de datos decide en cada petición =====

    [Fact]
    public async Task ADisabledOperator_LosesTheSessionOnTheNextRequest_AndTheEventIsLogged()
    {
        var platformOperator = await _fixture.CreateOperatorAsync();
        var token = await _fixture.PlatformTokenAsync(platformOperator);
        Assert.Equal(HttpStatusCode.OK, (await _fixture.ProbeAsync(token)).StatusCode);

        await _fixture.DisableOperatorAsync(platformOperator);

        // El token no ha caducado ni se ha tocado: aun así, la siguiente petición ya es un 403.
        Assert.Equal(HttpStatusCode.Forbidden, (await _fixture.ProbeAsync(token)).StatusCode);
        Assert.Contains(_fixture.Logs.Entries, entry =>
            entry.EventId == SecurityEventIds.PlatformSessionRevoked && entry.Message.Contains(platformOperator.UserId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADisabledOperator_CannotStartANewSession()
    {
        var platformOperator = await _fixture.CreateOperatorAsync();
        await _fixture.DisableOperatorAsync(platformOperator);

        var response = await _fixture.PlatformLoginAsync(platformOperator.Email, platformOperator.Password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ADisabledOperator_BetweenThePasswordAndTheCode_GetsNoSession()
    {
        var platformOperator = await _fixture.CreateOperatorAsync();
        var challenge = await _fixture.PlatformChallengeAsync(platformOperator);

        await _fixture.DisableOperatorAsync(platformOperator);
        var response = await _fixture.PlatformVerifyAsync(challenge, _fixture.FreshCode(platformOperator));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ===== R6: todos los fallos son el mismo 401 =====

    [Fact]
    public async Task EveryLoginFailure_IsTheSameResponse_WithTheSameCost()
    {
        var active = await _fixture.CreateOperatorAsync();
        var disabled = await _fixture.CreateOperatorAsync();
        await _fixture.DisableOperatorAsync(disabled);
        var company = await _fixture.NewMfaAccountAsync();
        var companyWithoutMfa = await _fixture.RegisterOwnerAsync();

        var attempts = new (string Name, string Email, string Password)[]
        {
            ("email desconocido", $"nadie-{Guid.NewGuid():N}@example.com", MembersFixture.NewPassword()),
            ("contraseña errónea", active.Email, "incorrecta-" + Guid.NewGuid().ToString("N")),
            ("operador dado de baja, contraseña correcta", disabled.Email, disabled.Password),
            ("cuenta de empresa con segundo factor, contraseña correcta", company.Email, company.Password),
            ("cuenta de empresa sin segundo factor, contraseña correcta", companyWithoutMfa.Email, companyWithoutMfa.Password)
        };

        var fingerprints = new List<string>();

        foreach (var (name, email, password) in attempts)
        {
            var before = _fixture.Checker.TotalVerifications;

            var response = await _fixture.PlatformLoginAsync(email, password);

            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{name}: respondió {(int)response.StatusCode}.");
            Assert.True(_fixture.Checker.TotalVerifications - before == 1, $"{name}: debía costar exactamente una verificación de contraseña.");
            fingerprints.Add(await MfaFlowFixture.FingerprintAsync(response));
        }

        Assert.Single(fingerprints.Distinct());
    }

    [Fact]
    public async Task EveryVerificationFailure_IsTheSameResponse()
    {
        var platformOperator = await _fixture.CreateOperatorAsync();
        var challenge = await _fixture.PlatformChallengeAsync(platformOperator);

        var failures = new[]
        {
            await _fixture.PlatformVerifyAsync(challenge, "000000"),
            await _fixture.PlatformVerifyAsync("desafio-inventado", _fixture.FreshCode(platformOperator)),
            await _fixture.PlatformVerifyAsync(challenge, "no es un código"),
            await _fixture.PlatformVerifyAsync(new string('A', 43), "ABCDEF")
        };

        var fingerprints = new List<string>();
        foreach (var failure in failures)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
            fingerprints.Add(await MfaFlowFixture.FingerprintAsync(failure));
        }

        Assert.Single(fingerprints.Distinct());
    }

    [Theory]
    [InlineData("", "123456")]
    [InlineData("desafio", "")]
    public async Task AnEmptyField_IsAValidationError_LikeOnTheCompanyDoor(string challenge, string code)
    {
        // Solo el tope de tamaño y el campo vacío se validan (ADR-009 R3, igual que en el canje de empresa): es un error del cliente,
        // no un intento de credencial. La forma exacta de un desafío o un código sí da el 401 de siempre (test anterior).
        Assert.Equal(HttpStatusCode.BadRequest, (await _fixture.PlatformVerifyAsync(challenge, code)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _fixture.VerifyAsync(challenge, code)).StatusCode);
    }

    // ===== R9: un desafío solo se canjea en su mundo =====

    [Fact]
    public async Task ACompanyChallenge_IsNotRedeemableOnThePlatform_AndIsNotSpentByTryingIt()
    {
        var company = await _fixture.NewMfaAccountAsync();
        var challenge = await _fixture.LoginForChallengeAsync(company);
        var code = _fixture.FreshCode(company);

        // Con el código CORRECTO, en la ruta de plataforma: se trata como un desafío inventado.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.PlatformVerifyAsync(challenge, code)).StatusCode);

        // Y no se ha gastado nada: en su mundo sigue valiendo.
        Assert.Equal(HttpStatusCode.OK, (await _fixture.VerifyAsync(challenge, code)).StatusCode);
    }

    [Fact]
    public async Task APlatformChallenge_IsNotRedeemableOnTheCompanies_AndIsNotSpentByTryingIt()
    {
        var platformOperator = await _fixture.CreateOperatorAsync();
        var challenge = await _fixture.PlatformChallengeAsync(platformOperator);
        var code = _fixture.FreshCode(platformOperator);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _fixture.VerifyAsync(challenge, code)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await _fixture.PlatformVerifyAsync(challenge, code)).StatusCode);
    }

    [Fact]
    public async Task TheCompanyLogin_NeverGivesAnOperatorACompanySession()
    {
        // R1: una cuenta de operador no tiene empresa. Con su contraseña y su código por la puerta de las empresas no sale una sesión de empresa.
        var platformOperator = await _fixture.CreateOperatorAsync();

        var login = await _fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = platformOperator.Email, password = platformOperator.Password });

        if (login.StatusCode == HttpStatusCode.OK)
        {
            var body = await MfaFlowFixture.JsonOf(login);
            Assert.False(body.TryGetProperty("accessToken", out _));

            var challenge = body.GetProperty("challenge").GetString()!;
            var verify = await _fixture.VerifyAsync(challenge, _fixture.FreshCode(platformOperator));
            Assert.Equal(HttpStatusCode.Unauthorized, verify.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        }
    }

    // ===== Límites de peticiones =====

    [Fact]
    public async Task ThePlatformLogin_IsLimitedPerIp()
    {
        var ip = TestClientIpStartupFilter.NewIp();
        var statuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var response = await _fixture.PostFromAsync(ip, "/api/v1/platform/auth/login", new { email = $"nadie-{attempt}@example.com", password = "incorrecta" });
            statuses.Add(response.StatusCode);
        }

        Assert.All(statuses.Take(5), status => Assert.Equal(HttpStatusCode.Unauthorized, status));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[5]);
    }
}
