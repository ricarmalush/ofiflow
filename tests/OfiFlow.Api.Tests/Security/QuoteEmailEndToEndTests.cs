using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OfiFlow.Api.Common;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Api.Tests.Errors;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Mail;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// Mandar un presupuesto por correo de principio a fin por HTTP (spec 026, bloque 2), con la API real, SQL Server real y dos empresas registradas de verdad. El transporte es uno que guarda lo enviado y
/// puede fallar a demanda (el SMTP de verdad se prueba en Infrastructure): lo que se prueba aquí es el camino completo alrededor de él — la respuesta, el mensaje y su PDF adjunto leído de vuelta, lo que
/// queda anotado, el aislamiento entre empresas, los límites y que ni la dirección ni nada del documento llegan al registro. Cada petición declara su propia IP (el límite por IP es otro test).
/// </summary>
public class QuoteEmailEndToEndTests(TwoTenantsFixture fixture) : IClassFixture<TwoTenantsFixture>, IAsyncLifetime
{
    private const string LegalNameOfA = "Fontanería Marta Gil S.L.";
    private const string LegalNameOfB = "Electricidad Juan Ruiz S.A.";
    private const string CompanyEmailOfA = "empresa-a@example.com";
    private const string Recipient = "cliente-final@ejemplo.es";

    private readonly RecordingEmailSender _email = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _a = null!;
    private HttpClient _b = null!;

    private HttpClient OriginalA => fixture.A.Client;

    public async Task InitializeAsync()
    {
        // La misma API y la misma base de datos, con el correo «activado»: un transporte que guarda lo enviado. Cada test tiene su propio host y por tanto sus propios límites.
        _factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(_email);
        }));

        _a = _factory.CreateClient();
        _a.DefaultRequestHeaders.Authorization = OriginalA.DefaultRequestHeaders.Authorization;
        _b = _factory.CreateClient();
        _b.DefaultRequestHeaders.Authorization = fixture.B.Client.DefaultRequestHeaders.Authorization;

        await SaveProfileAsync(OriginalA, LegalNameOfA, "B84531946", CompanyEmailOfA);
        await SaveProfileAsync(fixture.B.Client, LegalNameOfB, "A58818501", "empresa-b@example.com");
    }

    public Task DisposeAsync()
    {
        _a.Dispose();
        _b.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ---------- Preparación ----------

    private static async Task SaveProfileAsync(HttpClient client, string legalName, string taxId, string? email)
    {
        var response = await client.PutAsJsonAsync("/api/v1/company/profile", new { legalName, taxId, addressLine = "Calle de Alcalá 123, 2º B", postalCode = "28009", city = "Madrid", province = "Madrid", phone = "912345678", email });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task RemoveProfileAsync(Guid tenantId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CompanyProfiles.IgnoreQueryFilters().Where(p => p.TenantId == tenantId).ExecuteDeleteAsync();
    }

    private static async Task<Guid> CustomerAsync(HttpClient client, string name, string? notes = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/customers", new { type = 0, name, email = "cliente-ficha@ejemplo.es", phone = "600123456", address = "Calle Luna 3", notes });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static object Line(string description, object quantity, long price, int vat = 21) => new { description, quantity, unitPriceCents = price, vatPercent = vat };

    private static async Task<Guid> QuoteAsync(HttpClient client, Guid customerId, string title = "Sustitución de caldera", string? notes = "Pago a 30 días.", object[]? items = null, bool send = true)
    {
        var response = await client.PostAsJsonAsync("/api/v1/quotes", new { customerId, title, notes, validUntil = "2026-12-31", items = items ?? [Line("Caldera de gas", 1, 185000), Line("Mano de obra", 2.5m, 4500)] });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        if (send)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/quotes/{id}/send", null)).StatusCode);
        }

        return id;
    }

    private static async Task<JsonElement> ReadQuoteAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/v1/quotes/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }

    private static Task<HttpResponseMessage> EmailAsync(HttpClient client, Guid id, string? recipient = Recipient, string? ip = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/quotes/{id}/email") { Content = JsonContent.Create(new { recipient }) };
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, ip ?? TestClientIpStartupFilter.NewIp());
        return client.SendAsync(request);
    }

    private static async Task<PdfReading> DownloadAsync(HttpClient client, Guid id)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/quotes/{id}/pdf");
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, TestClientIpStartupFilter.NewIp());
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await PdfReading.OfAsync(response);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var problem = await Dictionary.ReadProblemAsync(response);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal(Dictionary.Spanish(code), problem.GetProperty("detail").GetString());
    }

    private static string LabelOf(JsonElement quote) => $"P-{quote.GetProperty("number").GetInt32():D4}";

    private static string Nbsp(string text) => text.Replace((char)0xA0, ' ');

    // ---------- El envío y su contenido ----------

    [Fact]
    public async Task ASentQuote_IsMailed_WithTheFixedTemplate_AndThePdfThatTheDownloadGives()
    {
        var customerId = await CustomerAsync(_a, "Construcciones Pérez e Hijos");
        var id = await QuoteAsync(_a, customerId);
        var quote = await ReadQuoteAsync(_a, id);

        var response = await EmailAsync(_a, id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = Assert.Single(_email.Sent);
        Assert.Equal(Recipient, message.To.Value);
        Assert.Equal($"Presupuesto {LabelOf(quote)}", message.Subject);
        Assert.Contains(LegalNameOfA, message.TextBody, StringComparison.Ordinal);
        Assert.Contains(LabelOf(quote), message.TextBody, StringComparison.Ordinal);
        Assert.Contains(PdfReading.Money(quote.GetProperty("totalCents").GetInt64()), Nbsp(message.TextBody), StringComparison.Ordinal);
        Assert.Equal(CompanyEmailOfA, message.ReplyTo!.Value);

        // El adjunto: un PDF de verdad, con el nombre que sale solo del número y el mismo contenido que la descarga.
        var attachment = message.Attachment!;
        Assert.Equal($"presupuesto-{LabelOf(quote)}.pdf", attachment.FileName);
        Assert.Equal("application/pdf", attachment.ContentType);
        var attached = PdfReading.Of(attachment.Content);
        var downloaded = await DownloadAsync(_a, id);
        Assert.Equal(downloaded.Pages, attached.Pages);
        Assert.Contains(LegalNameOfA, attached.Lines);
    }

    [Fact]
    public async Task TheResponse_CarriesOnlyTheMoment_AndIsNeverCached()
    {
        var id = await QuoteAsync(_a, await CustomerAsync(_a, "Cliente sin eco"));

        var response = await EmailAsync(_a, id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["emailedAt"], body.EnumerateObject().Select(p => p.Name).ToList());
        Assert.EndsWith("Z", body.GetProperty("emailedAt").GetString(), StringComparison.Ordinal);
        var raw = body.GetRawText();
        Assert.DoesNotContain("ejemplo", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(".pdf", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheQuoteShowsWhenItWasMailed_AndStaysSent_AndAResendUpdatesIt()
    {
        var id = await QuoteAsync(_a, await CustomerAsync(_a, "Cliente reenviado"));
        Assert.Equal(JsonValueKind.Null, (await ReadQuoteAsync(_a, id)).GetProperty("emailedAt").ValueKind);

        var first = (await (await EmailAsync(_a, id)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("emailedAt").GetDateTime();
        var afterFirst = await ReadQuoteAsync(_a, id);

        Assert.Equal("Sent", afterFirst.GetProperty("status").GetString());
        Assert.Equal(first, afterFirst.GetProperty("emailedAt").GetDateTime());
        Assert.False(afterFirst.TryGetProperty("emailedByUserId", out _), "Quién lo mandó se guarda para auditoría, pero no sale por la API.");

        await Task.Delay(50);
        var second = (await (await EmailAsync(_a, id, "otro-contacto@ejemplo.es")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("emailedAt").GetDateTime();

        Assert.True(second > first);
        Assert.Equal(second, (await ReadQuoteAsync(_a, id)).GetProperty("emailedAt").GetDateTime());
        Assert.Equal(2, _email.Sent.Count);
    }

    [Fact]
    public async Task ADraft_IsMailedToo_WithTheDraftMarkInEveryPage_AndStaysADraft()
    {
        var id = await QuoteAsync(_a, await CustomerAsync(_a, "Cliente de borrador"), send: false);

        Assert.Equal(HttpStatusCode.OK, (await EmailAsync(_a, id)).StatusCode);

        var pdf = PdfReading.Of(Assert.Single(_email.Sent).Attachment!.Content);
        Assert.All(pdf.Pages, page => Assert.Contains("BORRADOR", page, StringComparison.Ordinal));
        Assert.Equal("Draft", (await ReadQuoteAsync(_a, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task ASentQuote_HasNoDraftMark()
    {
        var id = await QuoteAsync(_a, await CustomerAsync(_a, "Cliente sin marca"));

        await EmailAsync(_a, id);

        Assert.DoesNotContain("BORRADOR", PdfReading.Of(Assert.Single(_email.Sent).Attachment!.Content).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MailingChangesNothingOfTheContent_ASentQuoteStillCannotBeEdited_AndTheTotalsAreTheSame()
    {
        var id = await QuoteAsync(_a, await CustomerAsync(_a, "Cliente intacto"));
        var before = await ReadQuoteAsync(_a, id);

        await EmailAsync(_a, id);
        var after = await ReadQuoteAsync(_a, id);

        foreach (var field in new[] { "totalCents", "netCents", "vatCents", "title", "status", "sentAt" })
        {
            Assert.Equal(before.GetProperty(field).GetRawText(), after.GetProperty(field).GetRawText());
        }

        var edit = await _a.PutAsJsonAsync($"/api/v1/quotes/{id}", new { version = after.GetProperty("version").GetString(), title = "Intento", items = new[] { Line("Otra", 1, 200) } });
        await AssertProblemAsync(edit, HttpStatusCode.BadRequest, "quote.not_editable");
    }

    // ---------- Texto hostil: nada de lo que teclea la persona entra en el correo ----------

    [Fact]
    public async Task HostileFreeText_NeverReachesTheSubjectOrTheBody_AndTheMailStillGoesOut()
    {
        const string hostile = "Reforma\r\nBcc: robo@malo.example\r\n\r\n<script>alert(1)</script>";
        var customerId = await CustomerAsync(_a, "Cliente <b>Hostil</b> \"Pérez\"", notes: "NOTA-INTERNA-secreta");
        var id = await QuoteAsync(_a, customerId, title: hostile, notes: hostile, items: [Line(hostile, 1, 12345)]);

        var response = await EmailAsync(_a, id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = Assert.Single(_email.Sent);
        var text = message.Subject + "\n" + message.TextBody;
        foreach (var secret in new[] { "Bcc", "malo.example", "script", "Hostil", "NOTA-INTERNA", "Reforma", "cliente-ficha", "600123456", "Calle Luna" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(Recipient, message.To.Value);
        Assert.DoesNotContain(message.Subject, char.IsControl);
    }

    // ---------- La dirección ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sin-arroba")]
    [InlineData("uno@ejemplo.es, dos@ejemplo.es")]
    [InlineData("uno@ejemplo.es;dos@ejemplo.es")]
    [InlineData("Nombre <a@ejemplo.es>")]
    [InlineData("a@ejemplo.es\r\nBcc: b@ejemplo.es")]
    public async Task AnInvalidOrMultipleAddress_IsAFieldError_AndNothingIsSent(string recipient)
    {
        var id = await QuoteAsync(_a, await CustomerAsync(_a, "Cliente de direcciones"));

        var response = await EmailAsync(_a, id, recipient);

        var problem = await Dictionary.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        Assert.Contains(problem.GetProperty("errors").EnumerateArray(), e => string.Equals(e.GetProperty("field").GetString(), "Recipient", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task ANullAddress_IsAFieldError_NotAServerError()
    {
        var id = await QuoteAsync(_a, await CustomerAsync(_a, "Cliente nulo"));

        var response = await EmailAsync(_a, id, recipient: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task TheFieldError_NeverRepeatsTheAddress()
    {
        var id = await QuoteAsync(_a, await CustomerAsync(_a, "Cliente discreto"));

        var response = await EmailAsync(_a, id, "dato-personal-ajeno@@ejemplo.es");

        Assert.DoesNotContain("dato-personal-ajeno", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ---------- Los rechazos de negocio ----------

    [Fact]
    public async Task AQuoteWithoutLines_IsRefused_AndNothingIsSent()
    {
        var id = await QuoteAsync(_a, await CustomerAsync(_a, "Cliente sin líneas"), items: [], send: false);

        await AssertProblemAsync(await EmailAsync(_a, id), HttpStatusCode.BadRequest, "quote.email_requires_items");

        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task ACompanyWithoutFiscalProfile_IsRefused_AndAfterSavingItTheSameQuoteGoesOut()
    {
        var id = await QuoteAsync(_b, await CustomerAsync(_b, "Cliente de B"));
        await RemoveProfileAsync(fixture.B.TenantId);

        await AssertProblemAsync(await EmailAsync(_b, id), HttpStatusCode.BadRequest, "quote.company_profile_incomplete");
        Assert.Empty(_email.Sent);

        await SaveProfileAsync(fixture.B.Client, LegalNameOfB, "A58818501", "empresa-b@example.com");
        Assert.Equal(HttpStatusCode.OK, (await EmailAsync(_b, id)).StatusCode);
        Assert.Single(_email.Sent);
    }

    [Fact]
    public async Task WithoutAMailServer_ItIsA400_AndNoPdfIsGenerated()
    {
        // El cliente de la fixture es el de la API de siempre: Email:Provider = None.
        var id = await QuoteAsync(OriginalA, await CustomerAsync(OriginalA, "Cliente sin correo"));

        await AssertProblemAsync(await EmailAsync(OriginalA, id), HttpStatusCode.BadRequest, "quote.email_disabled");

        Assert.Empty(_email.Sent);
        Assert.Equal(JsonValueKind.Null, (await ReadQuoteAsync(OriginalA, id)).GetProperty("emailedAt").ValueKind);
    }

    [Fact]
    public async Task ACompanyWithoutEmailInItsProfile_SendsWithoutAReplyTo()
    {
        await SaveProfileAsync(OriginalA, LegalNameOfA, "B84531946", email: null);
        var id = await QuoteAsync(_a, await CustomerAsync(_a, "Cliente sin respuesta"));

        Assert.Equal(HttpStatusCode.OK, (await EmailAsync(_a, id)).StatusCode);

        var message = Assert.Single(_email.Sent);
        Assert.Null(message.ReplyTo);
        Assert.Contains($"contacta con {LegalNameOfA}", message.TextBody, StringComparison.Ordinal);
    }

    // ---------- Cuando el servidor de correo falla ----------

    [Theory]
    [InlineData(EmailFailureCategory.Timeout)]
    [InlineData(EmailFailureCategory.Authentication)]
    [InlineData(EmailFailureCategory.Rejected)]
    [InlineData(EmailFailureCategory.Unavailable)]
    [InlineData(EmailFailureCategory.Unknown)]
    public async Task WhenTheMailServerFails_ItIsA502WithoutTheReason_NothingIsRecorded_AndTheEventKeepsTheCategory(EmailFailureCategory category)
    {
        var id = await QuoteAsync(_a, await CustomerAsync(_a, "Cliente con fallo"));
        _email.FailWith = category;
        var before = fixture.Factory.Logs.Entries.Count;

        var response = await EmailAsync(_a, id, "direccion-del-fallo@ejemplo.es");

        await AssertProblemAsync(response, HttpStatusCode.BadGateway, "quote.email_not_sent");
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("direccion-del-fallo", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(category.ToString(), raw, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, (await ReadQuoteAsync(_a, id)).GetProperty("emailedAt").ValueKind);

        var entries = fixture.Factory.Logs.Entries.Skip(before).Where(e => e.EventId == SecurityEventIds.QuoteEmailFailed).ToList();
        var entry = Assert.Single(entries);
        Assert.Contains(category.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.Contains(id.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("direccion-del-fallo", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterAFailure_ASecondTryGoesOut()
    {
        var id = await QuoteAsync(_a, await CustomerAsync(_a, "Cliente reintento"));
        _email.FailWith = EmailFailureCategory.Unavailable;
        Assert.Equal(HttpStatusCode.BadGateway, (await EmailAsync(_a, id)).StatusCode);

        _email.FailWith = null;
        Assert.Equal(HttpStatusCode.OK, (await EmailAsync(_a, id)).StatusCode);

        Assert.Single(_email.Sent);
        Assert.NotEqual(JsonValueKind.Null, (await ReadQuoteAsync(_a, id)).GetProperty("emailedAt").ValueKind);
    }

    // ---------- Aislamiento entre empresas ----------

    [Fact]
    public async Task AnotherCompanysQuote_Is404_LikeANonexistentOne_AndNothingIsSent()
    {
        var quoteOfB = await QuoteAsync(_b, await CustomerAsync(_b, "Cliente de B"));

        var foreign = await EmailAsync(_a, quoteOfB);
        var missing = await EmailAsync(_a, Guid.NewGuid());

        await AssertProblemAsync(foreign, HttpStatusCode.NotFound, "quote.not_found");
        await AssertProblemAsync(missing, HttpStatusCode.NotFound, "quote.not_found");
        Assert.Empty(_email.Sent);
        Assert.Equal(JsonValueKind.Null, (await ReadQuoteAsync(_b, quoteOfB)).GetProperty("emailedAt").ValueKind);
    }

    [Fact]
    public async Task EachCompanysMail_CarriesOnlyItsOwnFiscalData()
    {
        var quoteOfA = await QuoteAsync(_a, await CustomerAsync(_a, "Cliente de A"));
        var quoteOfB = await QuoteAsync(_b, await CustomerAsync(_b, "Cliente de B"));

        await EmailAsync(_a, quoteOfA, "para-a@ejemplo.es");
        await EmailAsync(_b, quoteOfB, "para-b@ejemplo.es");

        var mailOfA = Assert.Single(_email.Sent, m => m.To.Value == "para-a@ejemplo.es");
        var mailOfB = Assert.Single(_email.Sent, m => m.To.Value == "para-b@ejemplo.es");
        Assert.Contains(LegalNameOfA, mailOfA.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain(LegalNameOfB, mailOfA.TextBody, StringComparison.Ordinal);
        Assert.Contains(LegalNameOfB, mailOfB.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain(LegalNameOfA, mailOfB.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("B84531946", PdfReading.Of(mailOfB.Attachment!.Content).Text, StringComparison.Ordinal);
        Assert.Equal("empresa-b@example.com", mailOfB.ReplyTo!.Value);
    }

    [Fact]
    public async Task WithoutASession_ItIs401_AndNothingIsSent()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/quotes/{Guid.NewGuid()}/email") { Content = JsonContent.Create(new { recipient = Recipient }) };
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_email.Sent);
    }

    // ---------- El registro ----------

    [Fact]
    public async Task ASuccessfulMailing_LeavesEvent1410_AndNoLogEntryCarriesTheAddressOrTheDocument()
    {
        var marker = $"reservado{Guid.NewGuid():N}";
        await SaveProfileAsync(OriginalA, $"Nombre fiscal {marker}", "12345678Z", $"{marker}@fiscal.example");
        var customerId = await CustomerAsync(_a, $"Cliente {marker}", notes: $"Notas {marker}");
        var id = await QuoteAsync(_a, customerId, title: $"Título {marker}", notes: $"Notas {marker}", items: [Line($"Descripción {marker}", 1, 987654321)]);
        var before = fixture.Factory.Logs.Entries.Count;

        Assert.Equal(HttpStatusCode.OK, (await EmailAsync(_a, id, $"{marker}@destino.example")).StatusCode);
        await EmailAsync(_a, Guid.NewGuid(), $"{marker}@destino.example");   // un 404
        _email.FailWith = EmailFailureCategory.Rejected;
        await EmailAsync(_a, id, $"{marker}@destino.example");                // un 502

        var entries = fixture.Factory.Logs.Entries.Skip(before).ToList();
        var sent = Assert.Single(entries, e => e.EventId == SecurityEventIds.QuoteEmailSent);
        Assert.Contains(id.ToString(), sent.Message, StringComparison.Ordinal);
        Assert.Contains(fixture.A.TenantId.ToString(), sent.Message, StringComparison.Ordinal);
        Assert.Single(entries, e => e.EventId == SecurityEventIds.QuoteEmailFailed);

        foreach (var secret in new[] { marker, "destino.example", "12345678Z", "9.876.543,21", "987654321", "presupuesto-P-" })
        {
            Assert.DoesNotContain(entries, e => e.Message.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }

        Assert.DoesNotContain(entries, e => e.EventId == SecurityEventIds.UnhandledException);
    }

    // ---------- Los límites ----------

    [Fact]
    public async Task OnePersonCanMailTwentyTimesAnHour_AndTheNextIsRefused_WithTheUsualAnswer_AndNothingMoreGoesOut()
    {
        var id = await QuoteAsync(_a, await CustomerAsync(_a, "Cliente del límite"));

        for (var i = 0; i < QuoteEmailThrottle.PermitLimit; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await EmailAsync(_a, id)).StatusCode);
        }

        var refused = await EmailAsync(_a, id);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(refused.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.True(int.Parse(retryAfter.Single()) > 0);
        Assert.Equal("rate_limit.exceeded", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(QuoteEmailThrottle.PermitLimit, _email.Sent.Count);

        // Es por persona: la otra empresa sigue pudiendo mandar.
        var quoteOfB = await QuoteAsync(_b, await CustomerAsync(_b, "Cliente de B"));
        Assert.Equal(HttpStatusCode.OK, (await EmailAsync(_b, quoteOfB)).StatusCode);
    }

    [Fact]
    public void TheLimits_AreTheApprovedValues()
    {
        Assert.Equal(20, QuoteEmailThrottle.PermitLimit);
        Assert.Equal(TimeSpan.FromHours(1), QuoteEmailThrottle.Window);
        Assert.Equal("quote-email", RateLimiting.QuoteEmail);
    }

    [Fact]
    public async Task ThirtyOneRequestsAMinuteFromOneIp_GetA429OnTheLast_WhoeverSendsThem()
    {
        // Treinta caben (repartidas entre dos personas, para no tocar el límite por persona) y la 31 de la misma IP, no. Las peticiones fallan pronto (dirección inválida): no hace falta mandar nada.
        const string ip = "203.0.113.77";
        var quoteOfA = await QuoteAsync(_a, await CustomerAsync(_a, "Cliente IP A"));
        var quoteOfB = await QuoteAsync(_b, await CustomerAsync(_b, "Cliente IP B"));

        for (var i = 0; i < 30; i++)
        {
            var response = i % 2 == 0 ? await EmailAsync(_a, quoteOfA, "no-valida", ip) : await EmailAsync(_b, quoteOfB, "no-valida", ip);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var refused = await EmailAsync(_a, quoteOfA, "no-valida", ip);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(refused.Headers.Contains("Retry-After"));
        Assert.Empty(_email.Sent);
    }
}
