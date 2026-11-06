using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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
/// Pedir crear una cuenta por HTTP, con la API real y SQL Server real (spec 021, bloque 2). El transporte de correo es uno que guarda lo enviado, pero la cola y el servicio que la vacía son los
/// de verdad: se prueba el camino completo — petición, base de datos, cola, envío — y, sobre todo, que quien lo pide <b>no puede saber</b> si el email ya tiene cuenta, ni por la respuesta ni por el
/// tiempo, y que <b>no se crea ninguna cuenta</b> en ningún caso.
/// </summary>
public class RegistrationRequestEndToEndTests : IClassFixture<MembersFixture>, IAsyncLifetime
{
    private const string LinkBase = "https://app.ofiflow.example";
    private const string CompletePrefix = LinkBase + "/auth/complete-registration#token=";
    private const string Route = "/api/v1/auth/registration";
    private const string Version = "2026-11";

    private readonly MembersFixture _fixture;
    private readonly RecordingEmailSender _email = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public RegistrationRequestEndToEndTests(MembersFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        // La misma API y la misma base de datos que el resto, con el correo «activado» y las condiciones configuradas: el registro abierto.
        _factory = Open(_email);
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private WebApplicationFactory<Program> Open(RecordingEmailSender? email, string? termsVersion = Version, Action<IServiceCollection>? configure = null, params (string Key, string Value)[] settings) =>
        _fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Email:LinkBaseUrl", LinkBase);
            if (termsVersion is not null)
            {
                builder.UseSetting("Legal:TermsVersion", termsVersion);
            }

            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
            {
                if (email is not null)
                {
                    services.RemoveAll<IEmailSender>();
                    services.AddSingleton<IEmailSender>(email);
                }

                configure?.Invoke(services);
            });
        });

    private static string NewEmail(string prefix = "nueva") => $"{prefix}-{Guid.NewGuid():N}@example.com";

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

    private async Task<List<PendingRegistration>> PendingOfAsync(string email)
    {
        var normalized = email.Trim().ToUpperInvariant();
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Set<PendingRegistration>().AsNoTracking().Where(r => r.NormalizedEmail == normalized).OrderBy(r => r.CreatedAt).ToListAsync();
    }

    private static object Body(string email, string company = "Fontanería Demo SL", string person = "Ana Pérez", string? terms = Version) =>
        new { companyName = company, userName = person, email, acceptedTermsVersion = terms };

    // Cada petición declara su propia IP (como en los demás tests): el límite por IP no corta lo que no es su prueba.
    private Task<HttpResponseMessage> AskAsync(string email, string? ip = null, HttpClient? client = null) => SendAsync(Body(email), ip, client);

    private Task<HttpResponseMessage> SendAsync(object body, string? ip = null, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, ip ?? TestClientIpStartupFilter.NewIp());
        return (client ?? _client).SendAsync(request);
    }

    private static string LinkOf(EmailMessage message) =>
        message.TextBody.Split("\r\n").Single(line => line.StartsWith("https://", StringComparison.Ordinal));

    // El envío es asíncrono (la cola): se espera a que llegue, con un tope.
    private async Task<EmailMessage?> WaitForEmailToAsync(string email, int seconds = 10, RecordingEmailSender? sender = null)
    {
        var inbox = sender ?? _email;
        var limit = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < limit)
        {
            var found = inbox.Sent.FirstOrDefault(m => m.To.Value == email);
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(25);
        }

        return null;
    }

    // Y para comprobar que NO llegó: se espera un instante razonable antes de decir que no.
    private async Task<bool> NothingArrivesForAsync(string email, RecordingEmailSender? sender = null) => await WaitForEmailToAsync(email, seconds: 1, sender) is null;

    private async Task WaitForCountAsync(string email, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_email.Sent.Count(m => m.To.Value == email) < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }

    private static string Snapshot(HttpResponseMessage response) =>
        string.Join("|", response.Headers.Concat(response.Content.Headers)
            .Where(h => !string.Equals(h.Key, "Date", StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Select(h => $"{h.Key}={string.Join(",", h.Value)}"));

    // ===== Quien lo pide no puede saber si el email tiene cuenta (R1) =====

    [Fact]
    public async Task ANewAndAnExistingEmail_GetTheSameResponse_ByteForByte()
    {
        var existing = NewEmail("existe");
        await AccountAsync(existing);

        var withAccount = await AskAsync(existing);
        var withoutAccount = await AskAsync(NewEmail());

        Assert.Equal(HttpStatusCode.Accepted, withAccount.StatusCode);
        Assert.Equal(withAccount.StatusCode, withoutAccount.StatusCode);
        Assert.Empty(await withAccount.Content.ReadAsByteArrayAsync());
        Assert.Equal(await withAccount.Content.ReadAsByteArrayAsync(), await withoutAccount.Content.ReadAsByteArrayAsync());
        Assert.Equal(Snapshot(withAccount), Snapshot(withoutAccount));   // ni una cabecera distinta
        Assert.Equal("no-store", withAccount.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task AnOperatorsEmail_GetsTheSameResponse_AndTheNotice_ButNoPendingRegistration()
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
        var unknown = await AskAsync(NewEmail());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(Snapshot(unknown), Snapshot(response));
        var notice = await WaitForEmailToAsync(email);
        Assert.NotNull(notice);
        Assert.Equal("Ya tienes una cuenta de OfiFlow", notice.Subject);
        Assert.Empty(await PendingOfAsync(email));
    }

    [Fact]
    public async Task TheLimitedAndTheClosedCases_AlsoGetTheSameResponse()
    {
        // Pasarse del límite del email no se nota: la cuarta petición también es un 202 idéntico a la primera.
        var email = NewEmail();
        var first = await AskAsync(email);
        await AskAsync(email);
        await AskAsync(email);
        var limited = await AskAsync(email);

        Assert.Equal(HttpStatusCode.Accepted, limited.StatusCode);
        Assert.Equal(Snapshot(first), Snapshot(limited));
        Assert.Empty(await limited.Content.ReadAsByteArrayAsync());
    }

    // ===== Lo que se decide sin consultar nada: 400 (R3) =====

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

    [Fact]
    public async Task EmptyNames_AreRefused_AndNothingIsCreated()
    {
        var email = NewEmail();

        var response = await SendAsync(Body(email, company: "  ", person: ""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var fields = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("field").GetString()).ToList();
        Assert.Contains("CompanyName", fields);
        Assert.Contains("UserName", fields);
        Assert.Empty(await PendingOfAsync(email));
        Assert.True(await NothingArrivesForAsync(email));
    }

    [Theory]
    [InlineData(null)]          // no la envió
    [InlineData("")]
    [InlineData("2020-01")]     // una versión vieja: abrió el formulario antes de que cambiaran
    [InlineData("2026-11 ")]
    public async Task WithoutTheCurrentTermsVersion_ItIs400WithTheFieldCode_AndNothingIsCreatedNorSent(string? sent)
    {
        var email = NewEmail();

        var response = await SendAsync(Body(email, terms: sent));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateArray().ToList();
        var terms = Assert.Single(errors, e => e.GetProperty("field").GetString() == "AcceptedTermsVersion");
        Assert.Equal("registration.terms_not_accepted", terms.GetProperty("code").GetString());
        Assert.Empty(await PendingOfAsync(email));
        Assert.True(await NothingArrivesForAsync(email));
    }

    [Fact]
    public async Task TheTermsError_DoesNotDependOnWhetherTheEmailHasAnAccount()
    {
        var existing = NewEmail("existe");
        await AccountAsync(existing);

        var withAccount = await SendAsync(Body(existing, terms: "mala"));
        var withoutAccount = await SendAsync(Body(NewEmail(), terms: "mala"));

        Assert.Equal(HttpStatusCode.BadRequest, withAccount.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, withoutAccount.StatusCode);
        Assert.Equal(Snapshot(withAccount), Snapshot(withoutAccount));
    }

    // ===== El suelo de tiempo (R2, D2) =====

    [Fact]
    public async Task AllTheAnswers_TakeAtLeastTheFloor_SoTheTimeDoesNotGiveTheEmailAway()
    {
        var existing = NewEmail("existe");
        await AccountAsync(existing);
        var floor = TimeSpan.FromMilliseconds(250) - TimeSpan.FromMilliseconds(20); // tolerancia del temporizador

        var fresh = Stopwatch.StartNew();
        await AskAsync(NewEmail());
        fresh.Stop();
        var known = Stopwatch.StartNew();
        await AskAsync(existing);
        known.Stop();

        Assert.True(fresh.Elapsed >= floor, $"Con un email nuevo terminó antes del suelo: {fresh.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(known.Elapsed >= floor, $"Con un email con cuenta terminó antes del suelo: {known.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public async Task TheLimitedAnswer_AlsoTakesTheFloor()
    {
        var email = NewEmail();
        for (var i = 0; i < 3; i++)
        {
            await AskAsync(email);
        }

        var watch = Stopwatch.StartNew();
        await AskAsync(email);
        watch.Stop();

        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(230), $"La limitada terminó antes del suelo: {watch.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public async Task TheClosedAnswers_AlsoTakeTheFloor()
    {
        // Los caminos de «correo apagado» y «sin condiciones» casi no hacen trabajo: sin el suelo serían los más rápidos y distinguirían a un servidor sin configurar.
        using var emailOff = Open(email: null);
        using var noTerms = Open(_email, termsVersion: null);
        using var emailOffClient = emailOff.CreateClient();
        using var noTermsClient = noTerms.CreateClient();

        var off = Stopwatch.StartNew();
        await AskAsync(NewEmail(), client: emailOffClient);
        off.Stop();
        var closed = Stopwatch.StartNew();
        await AskAsync(NewEmail(), client: noTermsClient);
        closed.Stop();

        Assert.True(off.Elapsed >= TimeSpan.FromMilliseconds(230), $"Con el correo apagado terminó antes del suelo: {off.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(closed.Elapsed >= TimeSpan.FromMilliseconds(230), $"Sin condiciones terminó antes del suelo: {closed.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public async Task TheFloor_IsAppliedAfterTheWork_NotOnTopOfIt()
    {
        // Con un suelo de 900 ms y un trabajo que tarda 600 ms, la respuesta tarda ~900 ms. Si el suelo se esperara ANTES del trabajo, o se sumara a él, tardaría ~1500 ms y el tiempo
        // volvería a delatar el trabajo hecho. Los márgenes son anchos a propósito: es la única prueba con COTA SUPERIOR de tiempo (las demás solo exigen «no antes del suelo», que la
        // lentitud de la máquina no puede romper), y con una cota ajustada fallaba al correr toda la suite en paralelo.
        using var factory = Open(_email, configure: services =>
        {
            var original = services.Single(d => d.ServiceType == typeof(IPendingRegistrationService));
            services.Remove(original);
            services.AddScoped<IPendingRegistrationService>(provider => new SlowPendingRegistrations((IPendingRegistrationService)original.ImplementationFactory!(provider), TimeSpan.FromMilliseconds(600)));
        }, settings: [("Registration:ResponseFloor", "00:00:00.900")]);
        using var client = factory.CreateClient();

        // La primera petición a un host recién creado paga costes de arranque (modelo de EF, JIT, contenedor de servicios) que no son lo que se mide: se hace una y se descarta.
        await AskAsync(NewEmail(), client: client);

        var watch = Stopwatch.StartNew();
        var response = await AskAsync(NewEmail(), client: client);
        watch.Stop();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(880), $"Terminó antes del suelo: {watch.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(1250), $"El suelo se sumó al trabajo: {watch.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public void AnInvalidFloor_StopsTheApiFromStarting()
    {
        using var factory = Open(_email, settings: [("Registration:ResponseFloor", "-00:00:01")]);

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("Registration", exception.Message);
    }

    [Fact]
    public void AnInvalidTermsLabel_StopsTheApiFromStarting()
    {
        using var factory = Open(_email, termsVersion: "no vale <esto>");

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("Legal", exception.Message);
        Assert.DoesNotContain("no vale <esto>", exception.Message, StringComparison.Ordinal);
    }

    // ===== Email nuevo: el correo llega por la cola, con un enlace que es el del token guardado (R4) =====

    [Fact]
    public async Task WithANewEmail_TheEmailArrives_WithALinkWhoseTokenIsTheStoredHash()
    {
        var email = NewEmail();

        await SendAsync(Body(email, company: "Electricidad Gil SL", person: "Luis Gil"));

        var message = await WaitForEmailToAsync(email);
        Assert.NotNull(message);
        Assert.Equal("Crea tu cuenta de OfiFlow", message.Subject);
        var link = LinkOf(message);
        Assert.StartsWith(CompletePrefix, link);
        var token = link[CompletePrefix.Length..];
        Assert.True(OpaqueToken.IsWellFormed(token));

        var row = Assert.Single(await PendingOfAsync(email));
        Assert.Equal(OpaqueToken.Hash(token), row.TokenHash);     // el enlace del correo es el token cuyo hash está guardado
        Assert.NotEqual(token, row.TokenHash);
        Assert.True(row.IsUsableAt(DateTime.UtcNow));
        Assert.InRange(row.ExpiresAt - row.CreatedAt, TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1), TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        Assert.Equal("Electricidad Gil SL", row.CompanyName);
        Assert.Equal("Luis Gil", row.UserName);
        Assert.Equal(Version, row.TermsVersion);
        Assert.Equal(email, row.Email);
    }

    [Fact]
    public async Task TheToken_IsNowhereInTheRow_NotEvenInsideAnotherField()
    {
        var email = NewEmail();
        await AskAsync(email);
        var token = LinkOf((await WaitForEmailToAsync(email))!)[CompletePrefix.Length..];

        var row = Assert.Single(await PendingOfAsync(email));

        Assert.All(new[] { row.Email, row.NormalizedEmail, row.CompanyName, row.UserName, row.TermsVersion, row.TokenHash },
            value => Assert.DoesNotContain(token, value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRegistrationMessage_CarriesNothingOfWhatTheRequesterWrote()
    {
        // Quien pide el registro con el email de otra persona no consigue que OfiFlow le envíe un texto suyo.
        var email = NewEmail();
        var company = $"EmpresaDelAtacante-{Guid.NewGuid():N}";
        var person = $"PersonaDelAtacante-{Guid.NewGuid():N}";

        await SendAsync(Body(email, company, person));

        var message = await WaitForEmailToAsync(email);
        Assert.NotNull(message);
        Assert.DoesNotContain(company, message.TextBody);
        Assert.DoesNotContain(person, message.TextBody);
        Assert.DoesNotContain(company, message.Subject);
        Assert.DoesNotContain(person, message.Subject);
    }

    [Fact]
    public async Task NoAccountIsCreated_JustAPendingRegistration()
    {
        var email = NewEmail();

        await AskAsync(email);
        await WaitForEmailToAsync(email);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.Set<ApplicationUser>().AnyAsync(u => u.NormalizedEmail == email.ToUpperInvariant()));
        Assert.Single(await PendingOfAsync(email));
    }

    [Fact]
    public async Task ARequestWithAnotherHostAndOrigin_DoesNotChangeWhereTheLinkPoints()
    {
        var email = NewEmail();
        var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = JsonContent.Create(Body(email)) };
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, TestClientIpStartupFilter.NewIp());
        request.Headers.Host = "evil.example";
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("X-Forwarded-Host", "evil.example");

        await _client.SendAsync(request);

        var message = await WaitForEmailToAsync(email);
        Assert.NotNull(message);
        Assert.StartsWith(CompletePrefix, LinkOf(message));
        Assert.DoesNotContain("evil.example", message.TextBody);
    }

    // ===== Email que ya tiene cuenta: el aviso, sin token (R6) =====

    [Fact]
    public async Task WithAnExistingEmail_TheNoticeArrives_WithoutAnyToken_AndNothingIsStored()
    {
        var email = NewEmail("existe");
        await AccountAsync(email);

        await AskAsync(email);

        var message = await WaitForEmailToAsync(email);
        Assert.NotNull(message);
        Assert.Equal("Ya tienes una cuenta de OfiFlow", message.Subject);
        Assert.Equal($"{LinkBase}/auth/forgot-password", LinkOf(message));
        Assert.DoesNotContain("token", message.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await PendingOfAsync(email));
    }

    // ===== Un solo pendiente vigente (R5) =====

    [Fact]
    public async Task AskingTwice_LeavesOnlyTheLastLinkUsable()
    {
        var email = NewEmail();

        await AskAsync(email);
        await WaitForEmailToAsync(email);
        await AskAsync(email);
        await WaitForCountAsync(email, 2);

        var rows = await PendingOfAsync(email);
        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].IsUsableAt(DateTime.UtcNow));   // el del primer correo ya no vale
        Assert.True(rows[1].IsUsableAt(DateTime.UtcNow));
        var links = _email.Sent.Where(m => m.To.Value == email).Select(LinkOf).ToList();
        Assert.Equal(2, links.Distinct().Count());
    }

    [Fact]
    public async Task TheSameEmailInAnotherCase_IsTheSameEmail()
    {
        var email = NewEmail("Mixto");

        await AskAsync(email);
        await AskAsync(email.ToUpperInvariant());

        var rows = await PendingOfAsync(email);
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.UsedAt is null);
    }

    // ===== Los límites (R7) =====

    [Fact]
    public async Task TheFourthRequestOfTheHourForTheSameEmail_Gets202Too_ButNothingIsCreatedNorSent()
    {
        var email = NewEmail();

        // Desde IPs distintas: el límite por email no depende de la IP.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await AskAsync(email)).StatusCode);
        }

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.Accepted, status));   // la cuarta, también 202: no se nota
        await WaitForCountAsync(email, 3);
        await Task.Delay(500);
        Assert.Equal(3, _email.Sent.Count(m => m.To.Value == email));
        Assert.Equal(3, (await PendingOfAsync(email)).Count);
        Assert.Contains(_fixture.Factory.Logs.Entries, e => e.EventId == 1019);
    }

    [Fact]
    public async Task TheFourthNoticeOfTheHourForTheSameExistingEmail_Gets202Too_ButIsNotSent()
    {
        // Sin este límite, quien conociera un email con cuenta podría llenarle el buzón de avisos desde muchas IP.
        var email = NewEmail("existe");
        await AccountAsync(email);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await AskAsync(email)).StatusCode);
        }

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.Accepted, status));
        await WaitForCountAsync(email, 3);
        await Task.Delay(500);
        Assert.Equal(3, _email.Sent.Count(m => m.To.Value == email));
        Assert.Empty(await PendingOfAsync(email));
        Assert.Contains(_fixture.Factory.Logs.Entries, e => e.EventId == 1019);
    }

    [Fact]
    public async Task TheFourthRequestFromTheSameIp_Is429_AndSendsNothing()
    {
        var ip = TestClientIpStartupFilter.NewIp();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await AskAsync(NewEmail(), ip)).StatusCode);
        }

        var victim = NewEmail();
        var limited = await AskAsync(victim, ip);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));

        // La ventana es de UNA HORA (R7): el Retry-After lo dice. Con una ventana de un minuto el límite seguiría rechazando la cuarta, pero dejaría de proteger al buzón.
        var retryAfter = int.Parse(limited.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 3000, 3600);
        Assert.Equal("rate_limit.exceeded", (await limited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.True(await NothingArrivesForAsync(victim));
        Assert.Empty(await PendingOfAsync(victim));
    }

    [Fact]
    public async Task TheRegistrationBucket_IsNotTheOneOfTheOldRegistration()
    {
        // Cubos propios (hasta que el bloque 4 retire el registro inmediato): gastar uno no gasta el otro.
        var ip = TestClientIpStartupFilter.NewIp();
        for (var i = 0; i < 3; i++)
        {
            await AskAsync(NewEmail(), ip);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/forgot-password") { Content = JsonContent.Create(new { email = NewEmail() }) };
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, ip);
        var other = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, other.StatusCode);
    }

    // ===== Lo que queda en los logs (R20) =====

    [Fact]
    public async Task TheLogs_HaveTheRequestEvents_WithoutTheAddressesTheNamesTheTokensOrTheLinks()
    {
        var fresh = NewEmail("secreta");
        var existing = NewEmail("tampoco");
        var company = $"EmpresaPrivada-{Guid.NewGuid():N}";
        var person = $"PersonaPrivada-{Guid.NewGuid():N}";
        await AccountAsync(existing);
        await SendAsync(Body(fresh, company, person));
        var message = await WaitForEmailToAsync(fresh);
        await SendAsync(Body(existing, company, person));
        Assert.NotNull(message);
        var token = LinkOf(message)[CompletePrefix.Length..];

        var logs = _fixture.Factory.Logs.Entries;

        Assert.Contains(logs, e => e.EventId == 1018 && e.Message.Contains("True"));    // control positivo: los eventos existen
        Assert.Contains(logs, e => e.EventId == 1018 && e.Message.Contains("False"));
        var everything = string.Join('\n', logs.Select(e => e.Message));
        Assert.DoesNotContain(fresh, everything);
        Assert.DoesNotContain(existing, everything);
        Assert.DoesNotContain(company, everything);
        Assert.DoesNotContain(person, everything);
        Assert.DoesNotContain(token, everything);
        Assert.DoesNotContain(LinkBase, everything);
    }

    // ===== Registro cerrado (R8) =====

    [Fact]
    public async Task WithTheEmailOff_TheAnswerIsTheSame_NothingIsCreated_AndAWarningIsLeft()
    {
        using var factory = Open(email: null);
        using var client = factory.CreateClient();
        var email = NewEmail();

        var response = await AskAsync(email, client: client);
        var reference = await AskAsync(NewEmail());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(Snapshot(reference), Snapshot(response));
        Assert.Empty(await PendingOfAsync(email));
        Assert.Contains(_fixture.Factory.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Email:Provider = None") && e.Message.Contains("crear una cuenta"));
    }

    [Fact]
    public async Task WithoutTheTermsConfigured_TheAnswerIsTheSame_NothingIsCreatedNorSent_AndAWarningIsLeft()
    {
        var sender = new RecordingEmailSender();
        using var factory = Open(sender, termsVersion: null);
        using var client = factory.CreateClient();
        var email = NewEmail();

        // Cerrado, la versión que se envíe da igual: la respuesta es la misma y no cambia según un ajuste interno que quien llama no puede conocer.
        var response = await SendAsync(Body(email, terms: "lo-que-sea"), client: client);
        var reference = await AskAsync(NewEmail());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(Snapshot(reference), Snapshot(response));
        Assert.Empty(await PendingOfAsync(email));
        Assert.True(await NothingArrivesForAsync(email, sender));
        Assert.Contains(_fixture.Factory.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Legal:TermsVersion"));
    }

    // Un servicio de pendientes que tarda: para comprobar que el suelo de tiempo se aplica DESPUÉS del trabajo y no se suma a él.
    private sealed class SlowPendingRegistrations(IPendingRegistrationService inner, TimeSpan delay) : IPendingRegistrationService
    {
        public async Task<PendingRegistrationIssue> IssueAsync(string email, string companyName, string userName, string termsVersion, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return await inner.IssueAsync(email, companyName, userName, termsVersion, cancellationToken);
        }

        public Task<PendingRegistrationConsumption> ConsumeAsync(string token, CancellationToken cancellationToken) => inner.ConsumeAsync(token, cancellationToken);
    }
}
