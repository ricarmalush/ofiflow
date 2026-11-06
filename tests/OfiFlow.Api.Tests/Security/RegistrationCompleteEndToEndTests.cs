using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Mail;
using OfiFlow.Application.Common.Security;
using OfiFlow.Application.Tenancy;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// Completar el registro por HTTP, de principio a fin, con la API real y SQL Server real (spec 021, bloque 3). El flujo es el de verdad: se pide el registro, el enlace se lee del correo que
/// «llega» (la cola y el servicio que la vacía son los reales), se elige la contraseña y se entra con ella. Y, sobre todo, lo que no debe pasar: que <b>nazca algo</b> con un token que no sirve,
/// que los motivos se distingan desde fuera, que una carrera cree dos cuentas, o que quien registró el email de otra persona consiga algo.
/// </summary>
public class RegistrationCompleteEndToEndTests : IClassFixture<MembersFixture>, IAsyncLifetime
{
    private const string LinkBase = "https://app.ofiflow.example";
    private const string Marker = "#token=";
    private const string RequestRoute = "/api/v1/auth/registration";
    private const string CompleteRoute = "/api/v1/auth/registration/complete";
    private const string Chosen = "Clave-Elegida-Muy-Secreta-2026!";

    private readonly MembersFixture _fixture;
    private readonly RecordingEmailSender _email = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public RegistrationCompleteEndToEndTests(MembersFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
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

    // ===== Ayudas =====

    private static string NewEmail(string prefix = "nueva") => $"{prefix}-{Guid.NewGuid():N}@example.com";

    private static string Unknown() => Convert.ToBase64String(new byte[32]).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private Task<HttpResponseMessage> PostAsync(string route, object body, string? ip = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, ip ?? TestClientIpStartupFilter.NewIp());
        return _client.SendAsync(request);
    }

    private async Task<EmailMessage?> WaitForAsync(string email, int seconds = 10)
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

    private static string TokenOf(EmailMessage message)
    {
        var link = message.TextBody.Split("\r\n").Single(line => line.StartsWith("https://", StringComparison.Ordinal));
        return link[(link.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length)..];
    }

    /// <summary>Pide el registro y devuelve el token del correo que llega, como lo haría la persona.</summary>
    private async Task<string> RequestAsync(string email, string company, string person = "Ana Pérez")
    {
        var response = await PostAsync(RequestRoute, new { companyName = company, userName = person, email, acceptedTermsVersion = SqlServerApiFactory.TermsVersion });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var message = await WaitForAsync(email);
        Assert.NotNull(message);
        return TokenOf(message);
    }

    private Task<HttpResponseMessage> CompleteAsync(string token, string password = Chosen, string? ip = null) =>
        PostAsync(CompleteRoute, new { token, newPassword = password }, ip);

    private Task<HttpResponseMessage> LoginAsync(string email, string password) =>
        PostAsync("/api/v1/auth/login", new { email, password });

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return root.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>La respuesta completa (estado, cabeceras y cuerpo) sin el identificador de seguimiento, que es distinto en cada petición por diseño.</summary>
    private static async Task<string> FingerprintAsync(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(header => header.Key is not ("Date" or "Content-Length"))
            .OrderBy(header => header.Key, StringComparer.Ordinal)
            .Select(header => $"{header.Key}={string.Join(",", header.Value)}");
        var text = await response.Content.ReadAsStringAsync();
        var body = text.Length == 0
            ? string.Empty
            : string.Join(";", JsonDocument.Parse(text).RootElement.EnumerateObject().Where(property => property.Name != "traceId").Select(property => $"{property.Name}={property.Value}"));

        return $"{(int)response.StatusCode}|{string.Join(";", headers)}|{body}";
    }

    private async Task<T> InDbAsync<T>(Func<ApplicationDbContext, Task<T>> query)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    private Task<List<Tenant>> TenantsNamedAsync(string company) => InDbAsync(db => db.Tenants.AsNoTracking().Where(t => t.Name == company).ToListAsync());

    private Task<bool> HasAccountAsync(string email) =>
        InDbAsync(db => db.Set<ApplicationUser>().AnyAsync(u => u.NormalizedEmail == email.ToUpperInvariant()));

    private async Task CreateAccountAsync(string email)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IIdentityService>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True((await identity.CreateUserAsync(Guid.NewGuid(), email, MembersFixture.NewPassword(), CancellationToken.None)).Succeeded);
        await db.SaveChangesAsync();
    }

    private Task SetExpiryAsync(string email, DateTime expiresAt) =>
        InDbAsync(db => db.Set<PendingRegistration>().Where(r => r.NormalizedEmail == email.ToUpperInvariant()).ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, expiresAt)));

    /// <summary>Una invitación de verdad: una empresa nueva y su Owner invitan a un técnico. Con el correo apagado (la fábrica base), la respuesta lleva el token.</summary>
    private async Task<(InvitationCreatedDto Token, string Email)> InviteAsync()
    {
        var company = await _fixture.RegisterCompanyAsync($"Empresa {Guid.NewGuid():N}");
        var email = NewEmail("invitada");
        var response = await company.OwnerClient.PostAsJsonAsync("/api/v1/invitations", new { email, role = (int)TenantRole.Technician });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<InvitationCreatedDto>())!;
        Assert.NotNull(created.Token);
        return (created, email);
    }

    private IEnumerable<CapturingLoggerProvider.LogEntry> Events(int eventId) => _fixture.Factory.Logs.Entries.Where(e => e.EventId == eventId);

    // ===== El camino completo =====

    [Fact]
    public async Task TheWholeFlow_RequestTheLink_ChooseAPassword_AndSignInWithIt_ButNotBefore()
    {
        var email = NewEmail();
        var company = $"Empresa {Guid.NewGuid():N}";
        var token = await RequestAsync(email, company, "Ana Pérez");

        // Antes de abrir el enlace no hay cuenta (D1): ni la contraseña que se vaya a elegir ni ninguna otra entra.
        Assert.False(await HasAccountAsync(email));
        Assert.Empty(await TenantsNamedAsync(company));
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(email, Chosen)).StatusCode);

        var completed = await CompleteAsync(token);

        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        Assert.Empty(await completed.Content.ReadAsByteArrayAsync());               // R12: sin cuerpo ni sesión
        Assert.False(completed.Headers.Contains("Set-Cookie"));
        Assert.Equal("no-store", completed.Headers.CacheControl?.ToString());

        var login = await LoginAsync(email, Chosen);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        using var me = new HttpRequestMessage(HttpMethod.Get, "/api/v1/account/me");
        me.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        me.Headers.Add(TestClientIpStartupFilter.HeaderName, TestClientIpStartupFilter.NewIp());
        var profile = await (await _client.SendAsync(me)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(company, profile.GetProperty("tenantName").GetString());
        Assert.Equal("Owner", profile.GetProperty("role").GetString());
        Assert.Equal("Ana Pérez", profile.GetProperty("name").GetString());
    }

    [Fact]
    public async Task TheTermsAcceptedWhenRequesting_AreStoredInTheUser()
    {
        var email = NewEmail();
        var company = $"Empresa {Guid.NewGuid():N}";
        var before = DateTime.UtcNow.AddSeconds(-5);
        var token = await RequestAsync(email, company);

        Assert.Equal(HttpStatusCode.NoContent, (await CompleteAsync(token)).StatusCode);

        var tenant = Assert.Single(await TenantsNamedAsync(company));
        var user = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.ContactEmail == Domain.Common.Email.Create(email)));
        Assert.Equal(SqlServerApiFactory.TermsVersion, user.TermsVersion);
        Assert.InRange(user.TermsAcceptedAt!.Value, before, DateTime.UtcNow.AddSeconds(5));
        Assert.NotEqual(Guid.Empty, tenant.Id);
    }

    [Fact]
    public async Task TheLinkIsOneUse_TheSecondOpeningDoesNotCreateASecondCompany()
    {
        var email = NewEmail();
        var company = $"Empresa {Guid.NewGuid():N}";
        var token = await RequestAsync(email, company);
        Assert.Equal(HttpStatusCode.NoContent, (await CompleteAsync(token)).StatusCode);

        var again = await CompleteAsync(token, "Otra-clave-distinta-9");

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("registration.invalid", await CodeOfAsync(again));
        Assert.Single(await TenantsNamedAsync(company));
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(email, "Otra-clave-distinta-9")).StatusCode);
    }

    // ===== Un token que no sirve: el mismo 400 por cualquier motivo, y no nace nada =====

    [Fact]
    public async Task ATokenThatDoesNotServe_GivesTheSameCompleteResponse_WhateverTheReason()
    {
        // Control: cada motivo es real, y cada uno se prueba con su control positivo (el vigente sí sirve).
        var usedEmail = NewEmail("usado");
        var used = await RequestAsync(usedEmail, $"Empresa {Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NoContent, (await CompleteAsync(used)).StatusCode);

        var replacedEmail = NewEmail("sustituido");
        var replacedCompany = $"Empresa {Guid.NewGuid():N}";
        var replaced = await RequestAsync(replacedEmail, replacedCompany);
        var secondCount = _email.Sent.Count(m => m.To.Value == replacedEmail);
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(RequestRoute, new { companyName = replacedCompany, userName = "Ana", email = replacedEmail, acceptedTermsVersion = SqlServerApiFactory.TermsVersion })).StatusCode);
        var limit = DateTime.UtcNow.AddSeconds(10);
        while (_email.Sent.Count(m => m.To.Value == replacedEmail) <= secondCount && DateTime.UtcNow < limit)
        {
            await Task.Delay(25);
        }

        var expiredEmail = NewEmail("caducado");
        var expired = await RequestAsync(expiredEmail, $"Empresa {Guid.NewGuid():N}");
        await SetExpiryAsync(expiredEmail, DateTime.UtcNow.AddMinutes(-1));

        var takenEmail = NewEmail("ocupado");
        var taken = await RequestAsync(takenEmail, $"Empresa {Guid.NewGuid():N}");
        await CreateAccountAsync(takenEmail);                                              // alguien tomó el email entre medias

        var responses = new Dictionary<string, HttpResponseMessage>
        {
            ["unknown"] = await CompleteAsync(Unknown()),
            ["used"] = await CompleteAsync(used),
            ["replaced"] = await CompleteAsync(replaced),
            ["expired"] = await CompleteAsync(expired),
            ["emailTaken"] = await CompleteAsync(taken)
        };

        foreach (var (reason, response) in responses)
        {
            Assert.True(HttpStatusCode.BadRequest == response.StatusCode, $"{reason}: {response.StatusCode}");
            Assert.Equal("registration.invalid", await CodeOfAsync(response));
        }

        // Ni una pista de cuál es: la respuesta completa (estado, cabeceras y cuerpo) es la misma.
        var prints = new List<string>();
        foreach (var response in responses.Values)
        {
            prints.Add(await FingerprintAsync(response));
        }

        Assert.Single(prints.Distinct());
        Assert.Empty(await TenantsNamedAsync(replacedCompany));                           // y la sustituida no creó nada
        Assert.False(await HasAccountAsync(expiredEmail));
    }

    [Fact]
    public async Task ARejectedToken_LeavesTheRealReasonInTheLog_ButNeverTheTokenTheEmailOrThePassword()
    {
        var email = NewEmail();
        var token = await RequestAsync(email, $"Empresa {Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NoContent, (await CompleteAsync(token)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await CompleteAsync(token, "Contraseña-que-NO-debe-salir-9!")).StatusCode);

        var rejection = Events(SecurityEventIds.RegistrationRejected).Last(e => e.Message.Contains("UsedOrReplaced"));
        Assert.Contains("Motivo UsedOrReplaced", rejection.Message);
        foreach (var entry in _fixture.Factory.Logs.Entries)
        {
            Assert.DoesNotContain(token, entry.Message);
            Assert.DoesNotContain("Contraseña-que-NO-debe-salir-9!", entry.Message);
            Assert.DoesNotContain(Chosen, entry.Message);
            Assert.DoesNotContain(email, entry.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task EveryRejectionReasonIsLogged_WithItsOwnReal_Label()
    {
        var expiredEmail = NewEmail("caducado");
        var expired = await RequestAsync(expiredEmail, $"Empresa {Guid.NewGuid():N}");
        await SetExpiryAsync(expiredEmail, DateTime.UtcNow.AddMinutes(-1));
        var takenEmail = NewEmail("ocupado");
        var taken = await RequestAsync(takenEmail, $"Empresa {Guid.NewGuid():N}");
        await CreateAccountAsync(takenEmail);

        await CompleteAsync(Unknown());
        await CompleteAsync(expired);
        await CompleteAsync(taken);

        var reasons = Events(SecurityEventIds.RegistrationRejected).Select(e => e.Message).ToList();
        Assert.Contains(reasons, m => m.Contains("Motivo Unknown"));
        Assert.Contains(reasons, m => m.Contains("Motivo Expired"));
        Assert.Contains(reasons, m => m.Contains("Motivo EmailTaken"));
    }

    // ===== La contraseña y la forma del token se validan sin gastar nada =====

    [Fact]
    public async Task AMalformedToken_Or_ABadPassword_Give400_WithoutSpendingTheLink()
    {
        var email = NewEmail();
        var token = await RequestAsync(email, $"Empresa {Guid.NewGuid():N}");

        var malformed = await CompleteAsync("no-es-un-token");
        var tooShort = await CompleteAsync(token, "corta");
        var tooLong = await CompleteAsync(token, new string('a', 129));

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.False(await HasAccountAsync(email));

        // El token no se gastó con los intentos de validación fallida: con una contraseña buena, sirve (el control positivo).
        Assert.Equal(HttpStatusCode.NoContent, (await CompleteAsync(token)).StatusCode);
    }

    [Fact]
    public async Task AMalformedToken_HasTheSameCodeAsAnyOtherTokenFailure()
    {
        var malformed = await CompleteAsync("no-es-un-token");

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        var errors = (await malformed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateArray().ToList();
        var token = Assert.Single(errors, e => e.GetProperty("field").GetString() == "Token");
        Assert.Equal("registration.invalid", token.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ABodyWithoutFields_Gives400_NeverA500()
    {
        var response = await PostAsync(CompleteRoute, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ===== Quien registró el email de otra persona no consigue nada (D1, D5) =====

    [Fact]
    public async Task WhoRegisteredSomeoneElsesEmail_WithoutTheirMailbox_GetsNothing()
    {
        var victim = NewEmail("victima");
        var attackerCompany = $"Empresa del atacante {Guid.NewGuid():N}";

        // El atacante pide el registro con el email de otra persona. El enlace va al buzón de la víctima; él no lo ve.
        var victimToken = await RequestAsync(victim, attackerCompany, "Atacante");

        // No hay cuenta ni empresa: no puede entrar con ninguna contraseña (ni con las que él tendría en mente), ni completar adivinando.
        Assert.False(await HasAccountAsync(victim));
        Assert.Empty(await TenantsNamedAsync(attackerCompany));
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(victim, "Clave-del-atacante-1!")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CompleteAsync(Unknown(), "Clave-del-atacante-1!")).StatusCode);
        Assert.Empty(await TenantsNamedAsync(attackerCompany));

        // Si la víctima abre el enlace, la contraseña la elige ELLA: la del atacante no sirve nunca.
        Assert.Equal(HttpStatusCode.NoContent, (await CompleteAsync(victimToken, "Clave-de-la-victima-2026!")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(victim, "Clave-de-la-victima-2026!")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(victim, "Clave-del-atacante-1!")).StatusCode);
    }

    // ===== Una carrera de verdad: dos aperturas a la vez, una sola cuenta =====

    [Fact]
    public async Task TwoCompletionsWithTheSameToken_OneWins_AndThePasswordIsTheWinners()
    {
        // Varias rondas: el reparto de los hilos cambia, el resultado no.
        for (var round = 0; round < 6; round++)
        {
            var email = NewEmail();
            var company = $"Empresa carrera {round} {Guid.NewGuid():N}";
            var token = await RequestAsync(email, company);
            var gate = new TaskCompletionSource();

            async Task<(string Password, HttpResponseMessage Response)> Try(string password)
            {
                await gate.Task;
                return (password, await CompleteAsync(token, password));
            }

            var first = Try("Contraseña-de-la-primera-1!");
            var second = Try("Contraseña-de-la-segunda-2!");
            gate.SetResult();
            var results = await Task.WhenAll(first, second);

            Assert.Single(results, r => r.Response.StatusCode == HttpStatusCode.NoContent);
            var loser = Assert.Single(results, r => r.Response.StatusCode == HttpStatusCode.BadRequest);
            Assert.Equal("registration.invalid", await CodeOfAsync(loser.Response));

            var winner = results.Single(r => r.Response.StatusCode == HttpStatusCode.NoContent);
            Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, winner.Password)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(email, loser.Password)).StatusCode);

            // Y la perdedora no dejó nada: una sola empresa y una sola cuenta de acceso.
            Assert.Single(await TenantsNamedAsync(company));
            Assert.Equal(1, await InDbAsync(db => db.Set<ApplicationUser>().CountAsync(u => u.NormalizedEmail == email.ToUpperInvariant())));
        }
    }

    // ===== El límite por IP, con su cubo propio =====

    [Fact]
    public async Task TheEleventhCompletionFromTheSameIp_Is429_AndItNeverSpendsTheQuotaOfRequesting()
    {
        var ip = TestClientIpStartupFilter.NewIp();

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await CompleteAsync(Unknown(), Chosen, ip)).StatusCode);
        }

        var limited = await CompleteAsync(Unknown(), Chosen, ip);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limit.exceeded", await CodeOfAsync(limited));
        var retryAfter = int.Parse(limited.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 3000, 3600);

        // Cubo propio: pedir el registro desde esa misma IP sigue valiendo (su cupo es el de pedir, no el de completar).
        var request = await PostAsync(RequestRoute, new { companyName = "Empresa", userName = "Ana", email = NewEmail(), acceptedTermsVersion = SqlServerApiFactory.TermsVersion }, ip);
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
    }

    // ===== Los registros =====

    [Fact]
    public async Task TheCompletionIsLogged_WithTheNewIds_AndNothingPersonal()
    {
        var email = NewEmail();
        var company = $"Empresa Secretísima {Guid.NewGuid():N}";
        var token = await RequestAsync(email, company, "Persona Secretísima");
        Assert.Equal(HttpStatusCode.NoContent, (await CompleteAsync(token, "Contraseña-que-NO-debe-salir-9!")).StatusCode);

        var tenant = Assert.Single(await TenantsNamedAsync(company));
        var completed = Events(SecurityEventIds.RegistrationCompleted).Last(e => e.Message.Contains(tenant.Id.ToString()));
        Assert.Contains("nueva cuenta", completed.Message);

        foreach (var entry in _fixture.Factory.Logs.Entries)
        {
            Assert.DoesNotContain(token, entry.Message);
            Assert.DoesNotContain("Contraseña-que-NO-debe-salir-9!", entry.Message);
            Assert.DoesNotContain(email, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Secretísim", entry.Message);
        }
    }

    // ===== Las invitaciones también aceptan las condiciones (D8) =====

    [Fact]
    public async Task AcceptingAnInvitation_WithoutTheTermsVersion_Is400_WithTheFieldCode_AndTheTokenIsNotSpent()
    {
        var invitation = await InviteAsync();

        var withoutTerms = await PostAsync("/api/v1/auth/accept-invitation", new { token = invitation.Token.Token!, name = "Ana", password = MembersFixture.NewPassword() });
        var oldTerms = await PostAsync("/api/v1/auth/accept-invitation", new { token = invitation.Token.Token!, name = "Ana", password = MembersFixture.NewPassword(), acceptedTermsVersion = "2020-01" });

        foreach (var response in new[] { withoutTerms, oldTerms })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateArray().ToList();
            var terms = Assert.Single(errors, e => e.GetProperty("field").GetString() == "AcceptedTermsVersion");
            Assert.Equal("registration.terms_not_accepted", terms.GetProperty("code").GetString());
        }

        // Control positivo: con la versión vigente, el mismo token sirve, y la versión queda guardada en el usuario.
        var password = MembersFixture.NewPassword();
        var accepted = await PostAsync("/api/v1/auth/accept-invitation", new { token = invitation.Token.Token!, name = "Ana", password, acceptedTermsVersion = SqlServerApiFactory.TermsVersion });
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        var user = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.ContactEmail == Domain.Common.Email.Create(invitation.Email)));
        Assert.Equal(SqlServerApiFactory.TermsVersion, user.TermsVersion);
        Assert.NotNull(user.TermsAcceptedAt);
    }

    [Fact]
    public async Task TheTermsError_OfAnInvitation_DoesNotDependOnTheTokenBeingGood()
    {
        // Un error de CAMPO: la respuesta con un token inventado y la de uno real es la misma, así que no sirve para averiguar si una invitación existe.
        var invitation = await InviteAsync();

        var real = await PostAsync("/api/v1/auth/accept-invitation", new { token = invitation.Token.Token!, name = "Ana", password = MembersFixture.NewPassword(), acceptedTermsVersion = "mala" });
        var invented = await PostAsync("/api/v1/auth/accept-invitation", new { token = OpaqueToken.Generate().Value, name = "Ana", password = MembersFixture.NewPassword(), acceptedTermsVersion = "mala" });

        Assert.Equal(HttpStatusCode.BadRequest, real.StatusCode);
        Assert.Equal(await FingerprintAsync(real), await FingerprintAsync(invented));
    }
}
