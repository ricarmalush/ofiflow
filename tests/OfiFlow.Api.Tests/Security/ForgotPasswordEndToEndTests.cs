using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Mail;
using OfiFlow.Application.Common.Security;
using OfiFlow.Domain.Identity;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// «He olvidado mi contraseña» por HTTP, con la API real y SQL Server real (spec 018, bloque 2). El transporte es uno que guarda lo enviado, pero la cola y el servicio que la vacía
/// son los de verdad: se prueba el camino completo — petición, base de datos, cola, envío — y, sobre todo, que quien lo pide <b>no puede saber</b> si la cuenta existe.
/// </summary>
public class ForgotPasswordEndToEndTests : IClassFixture<MembersFixture>, IAsyncLifetime
{
    private const string LinkBase = "https://app.ofiflow.example";
    private const string ResetPrefix = LinkBase + "/auth/reset-password#token=";
    private const string Route = "/api/v1/auth/forgot-password";

    private readonly MembersFixture _fixture;
    private readonly RecordingEmailSender _email = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ForgotPasswordEndToEndTests(MembersFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        // La misma API y la misma base de datos que el resto, con el correo «activado»: un transporte que guarda lo enviado y el enlace base del despliegue.
        _factory = _fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Email:LinkBaseUrl", LinkBase);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(_email);
            });
        });
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static string NewEmail(string prefix = "olvidadiza") => $"{prefix}-{Guid.NewGuid():N}@example.com";

    private async Task<Guid> AccountAsync(string email)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IIdentityService>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var id = Guid.NewGuid();
        var created = await identity.CreateUserAsync(id, email, MembersFixture.NewPassword(), CancellationToken.None);
        Assert.True(created.Succeeded);
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<List<PasswordResetToken>> TokensOfAsync(Guid userId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Set<PasswordResetToken>().AsNoTracking().Where(t => t.UserId == userId).OrderBy(t => t.CreatedAt).ToListAsync();
    }

    // Cada petición declara su propia IP (como en los demás tests): el límite por IP no corta lo que no es su prueba.
    private Task<HttpResponseMessage> AskAsync(string email, string? ip = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = JsonContent.Create(new { email }) };
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, ip ?? TestClientIpStartupFilter.NewIp());
        return _client.SendAsync(request);
    }

    private static string LinkOf(EmailMessage message) =>
        message.TextBody.Split("\r\n").Single(line => line.StartsWith("https://", StringComparison.Ordinal));

    // El envío es asíncrono (la cola): se espera a que llegue, con un tope.
    private async Task<EmailMessage?> WaitForEmailToAsync(string email, int seconds = 10)
    {
        var limit = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < limit)
        {
            var found = _email.Sent.FirstOrDefault(m => m.To.Value == email);
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(25);
        }

        return null;
    }

    // Y para comprobar que NO llegó: se espera un instante razonable antes de decir que no.
    private async Task<bool> NothingArrivesForAsync(string email) => await WaitForEmailToAsync(email, seconds: 1) is null;

    // ===== Quien lo pide no puede saber si la cuenta existe (R1) =====

    private static string Snapshot(HttpResponseMessage response) =>
        string.Join("|", response.Headers.Concat(response.Content.Headers)
            .Where(h => !string.Equals(h.Key, "Date", StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Select(h => $"{h.Key}={string.Join(",", h.Value)}"));

    [Fact]
    public async Task AKnownAndAnUnknownEmail_GetTheSameResponse_ByteForByte()
    {
        var known = NewEmail();
        await AccountAsync(known);

        var withAccount = await AskAsync(known);
        var withoutAccount = await AskAsync(NewEmail("no-existe"));

        Assert.Equal(HttpStatusCode.Accepted, withAccount.StatusCode);
        Assert.Equal(withAccount.StatusCode, withoutAccount.StatusCode);
        Assert.Empty(await withAccount.Content.ReadAsByteArrayAsync());
        Assert.Equal(await withAccount.Content.ReadAsByteArrayAsync(), await withoutAccount.Content.ReadAsByteArrayAsync());
        Assert.Equal(Snapshot(withAccount), Snapshot(withoutAccount));   // ni una cabecera distinta
        Assert.Equal("no-store", withAccount.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task AnOperatorsEmail_GetsTheSameResponse_AndNoEmail()
    {
        var email = NewEmail("operadora");
        var userId = await AccountAsync(email);
        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Set<PlatformOperator>().Add(PlatformOperator.Create(userId, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        var response = await AskAsync(email);
        var unknown = await AskAsync(NewEmail("no-existe"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(Snapshot(unknown), Snapshot(response));
        Assert.True(await NothingArrivesForAsync(email));
        Assert.Empty(await TokensOfAsync(userId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sin-arroba")]
    [InlineData("a@b")]
    public async Task AMalformedEmail_Is400_DecidedWithoutLookingAnythingUp(string email)
    {
        var response = await AskAsync(email);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
    }

    // ===== El suelo de tiempo (R2, D2) =====

    [Fact]
    public async Task BothAnswers_TakeAtLeastTheFloor_SoTheTimeDoesNotGiveTheAccountAway()
    {
        // Lo que se MIDIÓ (ver la spec): sin el suelo, con cuenta ~17 ms y sin cuenta ~5 ms. Con él, ninguna de las dos termina antes de los 250 ms.
        var known = NewEmail();
        await AccountAsync(known);
        var floor = TimeSpan.FromMilliseconds(250) - TimeSpan.FromMilliseconds(20); // tolerancia del temporizador

        var withAccount = Stopwatch.StartNew();
        await AskAsync(known);
        withAccount.Stop();
        var withoutAccount = Stopwatch.StartNew();
        await AskAsync(NewEmail("no-existe"));
        withoutAccount.Stop();

        Assert.True(withAccount.Elapsed >= floor, $"Con cuenta terminó antes del suelo: {withAccount.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(withoutAccount.Elapsed >= floor, $"Sin cuenta terminó antes del suelo: {withoutAccount.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public async Task WithTheEmailOff_TheAnswerAlsoTakesTheFloor()
    {
        // El camino de «correo apagado» (R6) no hace casi trabajo: sin el suelo sería el más rápido de los tres y distinguiría a un servidor sin correo.
        using var client = _fixture.Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = JsonContent.Create(new { email = NewEmail() }) };
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, TestClientIpStartupFilter.NewIp());

        var watch = Stopwatch.StartNew();
        await client.SendAsync(request);
        watch.Stop();

        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(230), $"Terminó antes del suelo: {watch.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public void AnInvalidFloor_StopsTheApiFromStarting()
    {
        using var factory = _fixture.Factory.WithWebHostBuilder(builder => builder.UseSetting("ForgotPassword:ResponseFloor", "-00:00:01"));

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("ForgotPassword", exception.Message);
    }

    // ===== Con cuenta: el correo llega por la cola, con un enlace que es el del token guardado =====

    [Fact]
    public async Task WithAnAccount_TheEmailArrives_WithALinkWhoseTokenIsTheStoredHash()
    {
        var email = NewEmail();
        var userId = await AccountAsync(email);

        await AskAsync(email);

        var message = await WaitForEmailToAsync(email);
        Assert.NotNull(message);
        Assert.Equal("Restablece tu contraseña de OfiFlow", message.Subject);
        var link = LinkOf(message);
        Assert.StartsWith(ResetPrefix, link);
        var token = link[ResetPrefix.Length..];
        Assert.True(OpaqueToken.IsWellFormed(token));

        var row = Assert.Single(await TokensOfAsync(userId));
        Assert.Equal(OpaqueToken.Hash(token), row.TokenHash);     // el enlace del correo es el token cuyo hash está guardado
        Assert.NotEqual(token, row.TokenHash);
        Assert.True(row.IsUsableAt(DateTime.UtcNow));
    }

    [Fact]
    public async Task WithoutAnAccount_NoEmailIsSent_AndNothingIsStored()
    {
        var email = NewEmail("no-existe");

        await AskAsync(email);

        Assert.True(await NothingArrivesForAsync(email));
    }

    [Fact]
    public async Task ARequestWithAnotherHostAndOrigin_DoesNotChangeWhereTheLinkPoints()
    {
        var email = NewEmail();
        await AccountAsync(email);
        var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = JsonContent.Create(new { email }) };
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, TestClientIpStartupFilter.NewIp());
        request.Headers.Host = "evil.example";
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("X-Forwarded-Host", "evil.example");

        await _client.SendAsync(request);

        var message = await WaitForEmailToAsync(email);
        Assert.NotNull(message);
        Assert.StartsWith(ResetPrefix, LinkOf(message));
        Assert.DoesNotContain("evil.example", message.TextBody);
    }

    // ===== Un solo token vigente (R3) =====

    [Fact]
    public async Task AskingTwice_LeavesOnlyTheLastTokenUsable()
    {
        var email = NewEmail();
        var userId = await AccountAsync(email);

        await AskAsync(email);
        await WaitForEmailToAsync(email);
        await AskAsync(email);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_email.Sent.Count(m => m.To.Value == email) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        var rows = await TokensOfAsync(userId);
        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].IsUsableAt(DateTime.UtcNow));   // el del primer correo ya no vale
        Assert.True(rows[1].IsUsableAt(DateTime.UtcNow));
    }

    // ===== Los dos límites (R4) =====

    [Fact]
    public async Task TheFourthRequestOfTheHourForTheSameAccount_Gets202Too_ButNothingIsCreatedNorSent()
    {
        var email = NewEmail();
        var userId = await AccountAsync(email);

        // Desde IPs distintas: el límite por cuenta no depende de la IP.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await AskAsync(email)).StatusCode);
        }

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.Accepted, status));   // la cuarta, también 202: no se nota
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_email.Sent.Count(m => m.To.Value == email) < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        await Task.Delay(500);
        Assert.Equal(3, _email.Sent.Count(m => m.To.Value == email));
        Assert.Equal(3, (await TokensOfAsync(userId)).Count);
        Assert.Contains(_fixture.Factory.Logs.Entries, e => e.EventId == 1015 && e.Message.Contains(userId.ToString()));
    }

    [Fact]
    public async Task TheSixthRequestFromTheSameIp_Is429_AndSendsNothing()
    {
        var ip = TestClientIpStartupFilter.NewIp();
        var known = NewEmail();
        await AccountAsync(known);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await AskAsync(NewEmail("no-existe"), ip)).StatusCode);
        }

        var limited = await AskAsync(known, ip);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));

        // La ventana es de UNA HORA (R4): el Retry-After lo dice. Con una ventana de un minuto el límite seguiría rechazando el sexto pedido, pero dejaría de proteger al buzón.
        var retryAfter = int.Parse(limited.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 3000, 3600);
        Assert.Equal("rate_limit.exceeded", (await limited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.True(await NothingArrivesForAsync(known));
    }

    // ===== Lo que queda en los logs =====

    [Fact]
    public async Task TheLogs_HaveTheRequestEvents_WithoutTheAddressesTheTokensOrTheLinks()
    {
        var known = NewEmail("secreta");
        var unknown = NewEmail("tampoco");
        var userId = await AccountAsync(known);
        await AskAsync(known);
        var message = await WaitForEmailToAsync(known);
        await AskAsync(unknown);
        Assert.NotNull(message);
        var token = LinkOf(message)[ResetPrefix.Length..];

        var logs = _fixture.Factory.Logs.Entries;

        Assert.Contains(logs, e => e.EventId == 1014 && e.Message.Contains("True") && e.Message.Contains(userId.ToString()));   // control positivo: los eventos existen
        Assert.Contains(logs, e => e.EventId == 1014 && e.Message.Contains("False"));
        var everything = string.Join('\n', logs.Select(e => e.Message));
        Assert.DoesNotContain(known, everything);
        Assert.DoesNotContain(unknown, everything);
        Assert.DoesNotContain(token, everything);
        Assert.DoesNotContain(LinkBase, everything);
    }

    // ===== Sin correo configurado (R6) =====

    [Fact]
    public async Task WithTheEmailOff_TheAnswerIsTheSame_NoTokenIsCreated_AndAWarningIsLeft()
    {
        // La API de los demás tests, sin correo: el valor por defecto.
        var email = NewEmail();
        var userId = await AccountAsync(email);
        using var client = _fixture.Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = JsonContent.Create(new { email }) };
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, TestClientIpStartupFilter.NewIp());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(await TokensOfAsync(userId));
        Assert.Contains(_fixture.Factory.Logs.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("Email:Provider = None"));
    }
}
