using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Mail;
using OfiFlow.Domain.Identity;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// La API entera con SQL Server real, el reloj controlado y un transporte de correo que guarda lo enviado (spec 018, bloque 3). La cola y el servicio que la vacía son los de verdad:
/// el token se lee del correo que «llega», como lo haría la persona.
/// </summary>
public sealed class ResetFlowFixture : MfaFlowFixture
{
    public const string LinkBase = "https://app.ofiflow.example";

    public RecordingEmailSender Email { get; } = new();

    protected override void ConfigureHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Email:LinkBaseUrl", LinkBase);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Email);
        });
    }
}

/// <summary>
/// Restablecer la contraseña por HTTP, de principio a fin (spec 018, bloque 3): pedir el enlace, leerlo del correo, elegir la contraseña nueva y comprobar todo lo que debe cambiar
/// (la contraseña, las sesiones, el bloqueo, el aviso) y todo lo que NO debe cambiar (el segundo factor, la empresa suspendida, las cuentas de otros). Cada rechazo va con su control positivo.
/// </summary>
public class ResetPasswordEndToEndTests(ResetFlowFixture fixture) : IClassFixture<ResetFlowFixture>
{
    private const string ResetRoute = "/api/v1/auth/reset-password";
    private const string NewPassword = "Clave-Nueva-Muy-Secreta-2026!";
    private const string Marker = "#token=";

    // ===== Ayudas =====

    private async Task ForgotAsync(string email)
    {
        var response = await fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/forgot-password", new { email });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

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

    private static string TokenOf(EmailMessage message)
    {
        var link = message.TextBody.Split("\r\n").Single(line => line.StartsWith("https://", StringComparison.Ordinal));
        return link[(link.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length)..];
    }

    /// <summary>Pide el enlace y devuelve el token del correo que llega (el n-ésimo para esa dirección).</summary>
    private async Task<string> RequestTokenAsync(string email)
    {
        var already = fixture.Email.Sent.Count(m => m.To.Value == email && m.Subject == PasswordResetSubject);
        await ForgotAsync(email);
        var message = await WaitForAsync(email, PasswordResetSubject, skip: already);
        Assert.NotNull(message);
        return TokenOf(message);
    }

    private const string PasswordResetSubject = "Restablece tu contraseña de OfiFlow";

    private Task<HttpResponseMessage> ResetAsync(string token, string password = NewPassword, string? ip = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ResetRoute) { Content = JsonContent.Create(new { token, newPassword = password }) };
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, ip ?? TestClientIpStartupFilter.NewIp());
        return fixture.Http.SendAsync(request);
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;

    private Task<HttpResponseMessage> LoginAsync(string email, string password) =>
        fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password });

    private Task<HttpResponseMessage> MeAsync(string accessToken) =>
        fixture.SendAsync(HttpMethod.Get, "/api/v1/account/me", bearer: accessToken);

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        fixture.SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken });

    /// <summary>Como la huella de los demás tests, pero sin el identificador de seguimiento de la petición, que es distinto en cada una por diseño.</summary>
    private static async Task<string> FingerprintWithoutTraceAsync(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(header => header.Key is not ("Date" or "Content-Length"))
            .OrderBy(header => header.Key, StringComparer.Ordinal)
            .Select(header => $"{header.Key}={string.Join(",", header.Value)}");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.EnumerateObject()
            .Where(property => property.Name != "traceId")
            .Select(property => $"{property.Name}={property.Value}");

        return $"{(int)response.StatusCode}|{string.Join(";", headers)}|{string.Join(";", body)}";
    }

    private IEnumerable<CapturingLoggerProvider.LogEntry> Events(int eventId) => fixture.Logs.Entries.Where(e => e.EventId == eventId);

    // ===== El camino completo =====

    [Fact]
    public async Task TheWholeFlow_ChangesThePassword_ClosesTheSessions_AndSendsTheNotice()
    {
        var account = await fixture.RegisterOwnerAsync("Empresa del restablecimiento");
        var otherSession = await fixture.OpenExtraSessionTokenAsync(account);
        Assert.Equal(HttpStatusCode.OK, (await MeAsync(account.AccessToken)).StatusCode);    // controles: antes, las dos sesiones sirven
        Assert.Equal(HttpStatusCode.OK, (await MeAsync(otherSession)).StatusCode);

        var token = await RequestTokenAsync(account.Email);
        var reset = await ResetAsync(token);

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Empty(await reset.Content.ReadAsByteArrayAsync());
        Assert.Equal("no-store", reset.Headers.CacheControl?.ToString());

        // La contraseña nueva entra y la vieja ya no.
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(account.Email, NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(account.Email, account.Password)).StatusCode);

        // Todas las sesiones murieron: los access tokens (en la petición siguiente) y los refresh tokens.
        Assert.Equal(HttpStatusCode.Unauthorized, (await MeAsync(account.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await MeAsync(otherSession)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(account.RefreshToken)).StatusCode);

        // R12: el aviso, con su asunto fijo, a la dirección de la cuenta y sin enlace.
        var notice = await WaitForAsync(account.Email, PasswordChangedEmail());
        Assert.NotNull(notice);
        Assert.DoesNotContain("http", notice.TextBody, StringComparison.OrdinalIgnoreCase);
    }

    private static string PasswordChangedEmail() => OfiFlow.Application.Identity.PasswordChangedEmail.Subject;

    [Fact]
    public async Task TheNewPassword_CanBeResetAgain_ThroughANewRequest()
    {
        var account = await fixture.RegisterOwnerAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(await RequestTokenAsync(account.Email), "Primera-clave-nueva-1!")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(await RequestTokenAsync(account.Email), "Segunda-clave-nueva-2!")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(account.Email, "Segunda-clave-nueva-2!")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(account.Email, "Primera-clave-nueva-1!")).StatusCode);
    }

    [Fact]
    public async Task ResettingOneAccount_NeverClosesTheSessionsOfAnother()
    {
        var one = await fixture.RegisterOwnerAsync();
        var other = await fixture.RegisterOwnerAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(await RequestTokenAsync(one.Email))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await MeAsync(other.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(other.Email, other.Password)).StatusCode);
    }

    // ===== Un token que no sirve: el mismo error, siempre (R8) =====

    [Fact]
    public async Task ATokenThatDoesNotServe_GivesTheSame400_WhateverTheReason()
    {
        var account = await fixture.RegisterOwnerAsync();

        var used = await RequestTokenAsync(account.Email);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(used)).StatusCode);       // control: sirvió una vez

        var replaced = await RequestTokenAsync(account.Email);
        var current = await RequestTokenAsync(account.Email);                                // pedir otro anula el anterior
        var unknown = Convert.ToBase64String(new byte[32]).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var expired = await RequestTokenAsync((await fixture.RegisterOwnerAsync()).Email);   // otra cuenta: una sola pide como mucho 3 por hora (R4)
        fixture.Clock.Advance(TimeSpan.FromHours(1));                                        // caduca a la hora, en el instante exacto

        var responses = new Dictionary<string, HttpResponseMessage>
        {
            ["unknown"] = await ResetAsync(unknown),
            ["used"] = await ResetAsync(used),
            ["replaced"] = await ResetAsync(replaced),
            ["expired"] = await ResetAsync(expired)
        };

        foreach (var (reason, response) in responses)
        {
            Assert.True(HttpStatusCode.BadRequest == response.StatusCode, $"{reason}: {response.StatusCode}");
            Assert.Equal("password_reset.invalid", await CodeOfAsync(response));
        }

        // Ni una pista de cuál es: la respuesta completa (estado, cabeceras y cuerpo) es la misma.
        var prints = new List<string>();
        foreach (var response in responses.Values)
        {
            prints.Add(await FingerprintWithoutTraceAsync(response));
        }

        Assert.Single(prints.Distinct());
        _ = current;
    }

    [Fact]
    public async Task ARejectedToken_LeavesTheRealReasonInTheLog_ButNeverTheToken()
    {
        var account = await fixture.RegisterOwnerAsync();
        var token = await RequestTokenAsync(account.Email);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(token)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(token)).StatusCode);

        var rejection = Events(SecurityEventIds.PasswordResetRejected).Last(e => e.Message.Contains("UsedOrReplaced"));
        Assert.DoesNotContain(token, rejection.Message);
        Assert.Contains("Motivo UsedOrReplaced", rejection.Message);
    }

    [Fact]
    public async Task AMalformedToken_Or_AShortPassword_Give400_WithoutConsumingAnything()
    {
        var account = await fixture.RegisterOwnerAsync();
        var token = await RequestTokenAsync(account.Email);

        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync("no-es-un-token", NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(token, "corta")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(token, new string('a', 129))).StatusCode);

        // El token no se gastó con los intentos de validación fallida: con una contraseña buena, sirve.
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(token)).StatusCode);
    }

    [Fact]
    public async Task ABodyWithoutFields_Gives400_NeverA500()
    {
        var response = await fixture.SendAsync(HttpMethod.Post, ResetRoute, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ===== Lo que debe cambiar con el restablecimiento =====

    [Fact]
    public async Task ALockedAccount_CanSignInRightAfterResettingItsPassword()
    {
        var account = await fixture.RegisterOwnerAsync();
        for (var i = 0; i < AuthenticationPolicyLockoutThreshold; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(account.Email, "incorrecta")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(account.Email, account.Password)).StatusCode); // control: bloqueada, ni la buena entra

        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(await RequestTokenAsync(account.Email))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(account.Email, NewPassword)).StatusCode);
    }

    private const int AuthenticationPolicyLockoutThreshold = OfiFlow.Application.Identity.AuthenticationPolicy.LockoutThreshold;

    // ===== Lo que NO debe cambiar =====

    [Fact]
    public async Task AnAccountWithASecondFactor_StillNeedsTheCode_AfterResetting()
    {
        // R11, D7: controlar el buzón basta para cambiar la contraseña, no para entrar.
        var account = await fixture.NewMfaAccountAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(await RequestTokenAsync(account.Email))).StatusCode);

        var login = await LoginAsync(account.Email, NewPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await MfaFlowFixture.JsonOf(login);
        Assert.True(body.GetProperty("mfaRequired").GetBoolean());
        Assert.False(body.TryGetProperty("accessToken", out _));
        Assert.False(body.TryGetProperty("refreshToken", out _));

        // Y con el código, entra: el segundo factor sigue exactamente como estaba.
        var verify = await fixture.VerifyAsync(body.GetProperty("challenge").GetString()!, fixture.FreshCode(account));
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
    }

    [Fact]
    public async Task ASuspendedCompany_StillCannotSignIn_AfterItsPasswordChanges()
    {
        // R13: restablecer no desbloquea nada que no sea de la cuenta.
        const string company = "Empresa suspendida del restablecimiento";
        var account = await fixture.RegisterOwnerAsync(company);
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.Tenants.SingleAsync(t => t.Name == company)).Suspend(DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        var token = await RequestTokenAsync(account.Email);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(token)).StatusCode);       // la contraseña SÍ cambia

        var login = await LoginAsync(account.Email, NewPassword);
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("tenant.suspended", await CodeOfAsync(login));
    }

    // ===== Dos a la vez: gana uno (R10) =====

    [Fact]
    public async Task TwoResetsWithTheSameToken_OneWins_AndThePasswordIsTheWinners()
    {
        // Varias rondas: el reparto de los hilos cambia, el resultado no.
        for (var round = 0; round < 6; round++)
        {
            var account = await fixture.RegisterOwnerAsync();
            var token = await RequestTokenAsync(account.Email);
            var gate = new TaskCompletionSource();

            async Task<(string Password, HttpResponseMessage Response)> Try(string password)
            {
                await gate.Task;
                return (password, await ResetAsync(token, password));
            }

            var first = Try("Contraseña-de-la-primera-1!");
            var second = Try("Contraseña-de-la-segunda-2!");
            gate.SetResult();
            var results = await Task.WhenAll(first, second);

            Assert.Single(results, r => r.Response.StatusCode == HttpStatusCode.NoContent);
            var loser = Assert.Single(results, r => r.Response.StatusCode == HttpStatusCode.BadRequest);
            Assert.Equal("password_reset.invalid", await CodeOfAsync(loser.Response));

            var winner = results.Single(r => r.Response.StatusCode == HttpStatusCode.NoContent);
            Assert.Equal(HttpStatusCode.OK, (await LoginAsync(account.Email, winner.Password)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(account.Email, loser.Password)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(account.Email, account.Password)).StatusCode);

            // Un solo aviso: la perdedora no cambió nada y no avisa de nada.
            await Task.Delay(300);
            Assert.Single(fixture.Email.Sent, m => m.To.Value == account.Email && m.Subject == PasswordChangedEmail());
        }
    }

    // ===== El límite por IP =====

    [Fact]
    public async Task TheEleventhRequestFromTheSameIp_Is429()
    {
        var ip = TestClientIpStartupFilter.NewIp();
        var unknown = Convert.ToBase64String(new byte[32]).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(unknown, NewPassword, ip)).StatusCode);
        }

        var limited = await ResetAsync(unknown, NewPassword, ip);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limit.exceeded", await CodeOfAsync(limited));
        var retryAfter = int.Parse(limited.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 3000, 3600);
    }

    // ===== Los registros =====

    [Fact]
    public async Task TheLogs_HaveTheEvents_WithoutThePasswordTheTokenOrTheAddress()
    {
        var account = await fixture.RegisterOwnerAsync();
        var token = await RequestTokenAsync(account.Email);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(token, "Contraseña-que-NO-debe-salir-9!")).StatusCode);
        await ResetAsync(token, "Contraseña-que-NO-debe-salir-9!");                         // el segundo intento: rechazado
        await Task.Delay(300);

        var completed = Events(SecurityEventIds.PasswordResetCompleted).Where(e => e.Message.Contains("sesiones cerradas")).ToList();
        Assert.NotEmpty(completed);
        Assert.Contains("Se pidió restablecer", Events(SecurityEventIds.PasswordResetRequested).First().Message);

        foreach (var entry in fixture.Logs.Entries)
        {
            Assert.DoesNotContain("Contraseña-que-NO-debe-salir-9!", entry.Message);
            Assert.DoesNotContain(token, entry.Message);
            Assert.DoesNotContain(account.Email, entry.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
