using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Api.Tests.Errors;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// El PDF de un presupuesto de principio a fin por HTTP (spec 025, bloque 2), con la API real, SQL Server real y dos empresas registradas de verdad: el contenido leído de vuelta, los importes de la
/// API (los 15 casos fijos), la marca de borrador, el perfil incompleto, el aislamiento, las cabeceras, el texto hostil, el límite de peticiones y que nada del documento llega al registro. Cada negativa va
/// con su control positivo. Cada petición declara su propia IP (el límite de la descarga es por IP: el real se prueba aparte).
/// </summary>
public class QuotePdfEndToEndTests(TwoTenantsFixture fixture) : IClassFixture<TwoTenantsFixture>, IAsyncLifetime
{
    private const string LegalNameOfA = "Fontanería Marta Gil S.L.";
    private const string TaxIdOfA = "B84531946";
    private const string LegalNameOfB = "Electricidad Juan Ruiz S.A.";
    private const string TaxIdOfB = "A58818501";

    private HttpClient A => fixture.A.Client;

    private HttpClient B => fixture.B.Client;

    // Cada test empieza con la empresa A con su perfil guardado; los que necesitan otro estado lo cambian ellos mismos.
    public async Task InitializeAsync() => await SaveProfileAsync(A, LegalNameOfA, TaxIdOfA);

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------- Preparación ----------

    private static async Task SaveProfileAsync(HttpClient client, string legalName, string taxId, string city = "Madrid")
    {
        var response = await client.PutAsJsonAsync("/api/v1/company/profile", new
        {
            legalName,
            taxId,
            addressLine = "Calle de Alcalá 123, 2º B",
            postalCode = "28009",
            city,
            province = "Madrid",
            phone = "912345678",
            email = "empresa@example.com"
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task ResetProfileAsync(Guid tenantId)
    {
        // El perfil nace al guardarlo: para probar «una empresa que todavía no lo tiene» se borra su fila directamente (no hay operación de borrado en la API).
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CompanyProfiles.IgnoreQueryFilters().Where(p => p.TenantId == tenantId).ExecuteDeleteAsync();
    }

    private static async Task<Guid> CustomerAsync(HttpClient client, string name, string? address = null, string? notes = null, string? email = null, string? phone = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/customers", new { type = 0, name, email, phone, address, notes });
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

    private static Task<HttpResponseMessage> PdfAsync(HttpClient client, Guid id, string? ip = null, string query = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/quotes/{id}/pdf{query}");
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, ip ?? TestClientIpStartupFilter.NewIp());
        return client.SendAsync(request);
    }

    private static async Task<PdfReading> ReadPdfAsync(HttpClient client, Guid id)
    {
        var response = await PdfAsync(client, id);
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

    private static string Qty(decimal quantity)
    {
        // Escrita aparte de la de producción: «1.234,5» con punto de millares y coma decimal.
        var text = quantity.ToString("#,##0.###", CultureInfo.InvariantCulture);
        return text.Replace(',', '#').Replace('.', ',').Replace('#', '.');
    }

    // ---------- El contenido (R4) ----------

    [Fact]
    public async Task ASentQuote_GivesAPdfWithTheIssuerTheCustomerTheLinesAndTheTotals()
    {
        var customerId = await CustomerAsync(A, "Construcciones Pérez e Hijos", address: "Avenida de América 45\n28028 Madrid");
        var id = await QuoteAsync(A, customerId);
        var quote = await ReadQuoteAsync(A, id);
        var label = $"P-{quote.GetProperty("number").GetInt32():D4}";

        var response = await PdfAsync(A, id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pdf = await PdfReading.OfAsync(response);
        var lines = pdf.Lines;

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf.Bytes, 0, 5));
        Assert.Contains(LegalNameOfA, lines);
        Assert.Contains($"NIF {TaxIdOfA}", lines);
        Assert.Contains("Calle de Alcalá 123, 2º B", lines);
        Assert.Contains("28009 Madrid (Madrid)", lines);
        Assert.Contains("Tel. 912345678", lines);
        Assert.Contains("empresa@example.com", lines);
        Assert.Contains("PRESUPUESTO", lines);
        Assert.Contains(label, lines);
        Assert.Contains("Sustitución de caldera", lines);
        Assert.Contains("Válido hasta: 31/12/2026", lines);
        Assert.Contains("Construcciones Pérez e Hijos", lines);
        Assert.Contains("Avenida de América 45", lines);
        Assert.Contains("28028 Madrid", lines);
        Assert.Contains($"Caldera de gas 1 {PdfReading.Money(185000)} 21 % {PdfReading.Money(185000)}", lines);
        Assert.Contains($"Mano de obra 2,5 {PdfReading.Money(4500)} 21 % {PdfReading.Money(11250)}", lines);
        Assert.Contains($"TOTAL {PdfReading.Money(quote.GetProperty("totalCents").GetInt64())}", lines);
        Assert.Contains("Pago a 30 días.", lines);
        Assert.DoesNotContain("BORRADOR", pdf.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheResponseIsAnAttachmentOfTypePdf_NeverCached_AndNamedByTheNumberAlone()
    {
        // Un título y un cliente con comillas, saltos de línea y «../»: ninguno puede llegar a una cabecera.
        var customerId = await CustomerAsync(A, "Cliente \"raro\" ../../etc");
        var id = await QuoteAsync(A, customerId, title: "Reforma \"baño\" ../../x\r\nX-Inyectada: 1");
        var number = (await ReadQuoteAsync(A, id)).GetProperty("number").GetInt32();

        var response = await PdfAsync(A, id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal($"presupuesto-P-{number:D4}.pdf", response.Content.Headers.ContentDisposition.FileName?.Trim('"'));
        Assert.False(response.Headers.Contains("X-Inyectada"));
        var rawDisposition = string.Join(" ", response.Content.Headers.GetValues("Content-Disposition"));
        foreach (var forbidden in new[] { "Reforma", "baño", "..", "raro", "etc" })
        {
            Assert.DoesNotContain(forbidden, rawDisposition, StringComparison.Ordinal);
        }

        Assert.Equal(response.Content.Headers.ContentLength, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task ADraft_CarriesTheDraftMarkOnEveryPage_AndAnIssuedQuoteOnNone()
    {
        var customerId = await CustomerAsync(A, "Cliente de borrador");
        var many = Enumerable.Range(1, 100).Select(i => Line($"Partida número {i} con una descripción larga para ocupar espacio", 1, 1000 + i)).ToArray();
        var draftId = await QuoteAsync(A, customerId, items: many, send: false);
        var sentId = await QuoteAsync(A, customerId, items: many, send: true);

        var draft = await ReadPdfAsync(A, draftId);
        var sent = await ReadPdfAsync(A, sentId);

        Assert.True(draft.PageCount > 1);
        Assert.All(draft.Pages, page => Assert.Contains("BORRADOR", page, StringComparison.Ordinal));
        Assert.All(sent.Pages, page => Assert.DoesNotContain("BORRADOR", page, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAcceptedAndARejectedQuote_HavePdfWithoutTheDraftMark()
    {
        var customerId = await CustomerAsync(A, "Cliente que decide");
        var accepted = await QuoteAsync(A, customerId);
        var rejected = await QuoteAsync(A, customerId);
        Assert.Equal(HttpStatusCode.NoContent, (await A.PostAsync($"/api/v1/quotes/{accepted}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await A.PostAsync($"/api/v1/quotes/{rejected}/reject", null)).StatusCode);

        foreach (var id in new[] { accepted, rejected })
        {
            var pdf = await ReadPdfAsync(A, id);
            Assert.DoesNotContain("BORRADOR", pdf.Text, StringComparison.Ordinal);
            Assert.Contains("TOTAL", pdf.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheCustomerOfTheDocument_IsOnlyItsNameAndAddress_NeverItsInternalNotesEmailOrPhone()
    {
        var customerId = await CustomerAsync(A, "Cliente discreto", address: "Calle Luna 3", notes: "NOTA-INTERNA-no-debe-salir", email: "discreto@example.com", phone: "600111222");
        var id = await QuoteAsync(A, customerId);

        var pdf = await ReadPdfAsync(A, id);

        Assert.Contains("Cliente discreto", pdf.Lines);
        Assert.Contains("Calle Luna 3", pdf.Lines);
        foreach (var secret in new[] { "NOTA-INTERNA", "discreto@example.com", "600111222" })
        {
            Assert.DoesNotContain(secret, pdf.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ThePdfOfAQuoteWithoutNotesNorValidityHasNeither()
    {
        var customerId = await CustomerAsync(A, "Cliente sin extras");
        var response = await A.PostAsJsonAsync("/api/v1/quotes", new { customerId, title = "Sin extras", items = new[] { Line("Algo", 1, 100) } });
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var pdf = await ReadPdfAsync(A, id);

        Assert.DoesNotContain("Válido hasta", pdf.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Condiciones", pdf.Text, StringComparison.Ordinal);
    }

    // ---------- Los importes son los de la API (R5): los 15 casos fijos de la spec 023 ----------

    private sealed record Vector(string Name, List<(decimal Quantity, long Price, int Vat, int Repeat)> Lines);

    private static List<Vector> LoadVectors()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Quotes", "QuoteTotalsVectors.json")));

        return [.. document.RootElement.EnumerateArray().Select(e => new Vector(
            e.GetProperty("name").GetString()!,
            [.. e.GetProperty("lines").EnumerateArray().Select(l => (l.GetProperty("quantity").GetDecimal(), l.GetProperty("unitPriceCents").GetInt64(), l.GetProperty("vatPercent").GetInt32(), l.TryGetProperty("repeat", out var repeat) ? repeat.GetInt32() : 1))]))];
    }

    public static TheoryData<int> VectorIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < LoadVectors().Count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Fact]
    public void TheFixedVectorsAreTheFifteenOfTheSpec023()
    {
        Assert.Equal(15, LoadVectors().Count);
    }

    [Theory]
    [MemberData(nameof(VectorIndexes))]
    public async Task ThePdfPrintsExactlyTheAmountsThatTheApiReturns_ForEachFixedVector(int index)
    {
        var vector = LoadVectors()[index];
        var items = vector.Lines.SelectMany(l => Enumerable.Repeat(l, l.Repeat)).Select((l, i) => Line($"Línea {i + 1}", l.Quantity, l.Price, l.Vat)).ToArray();
        var customerId = await CustomerAsync(A, $"Cliente del caso {index}");
        var id = await QuoteAsync(A, customerId, items: items, send: false);

        var quote = await ReadQuoteAsync(A, id);
        var lines = (await ReadPdfAsync(A, id)).Lines;

        Assert.Contains($"Base imponible {PdfReading.Money(quote.GetProperty("netCents").GetInt64())}", lines);
        Assert.Contains($"IVA {PdfReading.Money(quote.GetProperty("vatCents").GetInt64())}", lines);
        Assert.Contains($"TOTAL {PdfReading.Money(quote.GetProperty("totalCents").GetInt64())}", lines);
        foreach (var b in quote.GetProperty("vatBreakdown").EnumerateArray())
        {
            Assert.Contains($"{b.GetProperty("vatPercent").GetInt32()} % {PdfReading.Money(b.GetProperty("baseCents").GetInt64())} {PdfReading.Money(b.GetProperty("vatCents").GetInt64())}", lines);
        }

        foreach (var item in quote.GetProperty("items").EnumerateArray())
        {
            var expected = $"{item.GetProperty("description").GetString()} {Qty(item.GetProperty("quantity").GetDecimal())} {PdfReading.Money(item.GetProperty("unitPriceCents").GetInt64())} {item.GetProperty("vatPercent").GetInt32()} % {PdfReading.Money(item.GetProperty("netCents").GetInt64())}";
            Assert.Contains(expected, lines);
        }
    }

    // ---------- Los datos fiscales de la empresa (R3) ----------

    [Fact]
    public async Task WithoutAFiscalProfile_ThereIsNoPdf_AndOnceSavedTheSameQuoteGivesOne()
    {
        var id = await QuoteAsync(A, await CustomerAsync(A, "Cliente sin perfil"));
        await ResetProfileAsync(fixture.A.TenantId);

        var without = await PdfAsync(A, id);
        await AssertProblemAsync(without, HttpStatusCode.BadRequest, "quote.company_profile_incomplete");
        Assert.Contains("no-store", without.Headers.CacheControl!.ToString());
        Assert.NotEqual("application/pdf", without.Content.Headers.ContentType?.MediaType);

        await SaveProfileAsync(A, LegalNameOfA, TaxIdOfA);
        var with = await PdfAsync(A, id);

        Assert.Equal(HttpStatusCode.OK, with.StatusCode);
        Assert.Equal("application/pdf", with.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TheAnswerToAMissingQuote_IsTheSameWithOrWithoutAProfile()
    {
        // El 404 va antes que el 400: quien pregunta por un presupuesto que no existe no se entera de si la empresa tiene perfil.
        var missing = Guid.NewGuid();

        var withProfile = await PdfAsync(A, missing);
        await ResetProfileAsync(fixture.A.TenantId);
        var withoutProfile = await PdfAsync(A, missing);

        await AssertProblemAsync(withProfile, HttpStatusCode.NotFound, "quote.not_found");
        await AssertProblemAsync(withoutProfile, HttpStatusCode.NotFound, "quote.not_found");
    }

    [Fact]
    public async Task ChangingTheFiscalProfileLaterChangesTheIssuerOfTheNextPdf()
    {
        // Riesgo aceptado de la spec (D4): el PDF no se guarda, así que sale con los datos fiscales de ahora.
        var id = await QuoteAsync(A, await CustomerAsync(A, "Cliente fijo"));

        await SaveProfileAsync(A, "Nombre Anterior S.L.", TaxIdOfA);
        var before = await ReadPdfAsync(A, id);
        await SaveProfileAsync(A, "Nombre Nuevo S.L.", TaxIdOfA);
        var after = await ReadPdfAsync(A, id);

        Assert.Contains("Nombre Anterior S.L.", before.Lines);
        Assert.Contains("Nombre Nuevo S.L.", after.Lines);
        Assert.DoesNotContain("Nombre Anterior S.L.", after.Text, StringComparison.Ordinal);
    }

    // ---------- Sesión y aislamiento ----------

    [Fact]
    public async Task WithoutASession_ThereIsA401_AndWithOneTheSameRouteWorks()
    {
        var id = await QuoteAsync(A, await CustomerAsync(A, "Cliente con sesión"));

        var anonymous = await PdfAsync(fixture.Anonymous, id);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PdfAsync(A, id)).StatusCode);
    }

    [Fact]
    public async Task AnotherCompany_CannotDownloadThePdfOfAQuoteOfThisOne_AndGetsTheSame404AsForAMissingOne()
    {
        await SaveProfileAsync(B, LegalNameOfB, TaxIdOfB);
        var idOfA = await QuoteAsync(A, await CustomerAsync(A, "Cliente de A"));

        var foreign = await PdfAsync(B, idOfA);
        var missing = await PdfAsync(B, Guid.NewGuid());

        var foreignProblem = await Dictionary.ReadProblemAsync(foreign);
        var missingProblem = await Dictionary.ReadProblemAsync(missing);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // La misma respuesta campo a campo (salvo el identificador de la traza, que es distinto en cada petición): no se puede distinguir «de otra empresa» de «no existe».
        string[] Fields(JsonElement problem) => [.. problem.EnumerateObject().Where(p => p.Name != "traceId").OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => $"{p.Name}={p.Value}")];
        Assert.Equal(Fields(missingProblem), Fields(foreignProblem));
        Assert.Equal("quote.not_found", foreignProblem.GetProperty("code").GetString());

        // Control positivo: A sí lo descarga.
        Assert.Equal(HttpStatusCode.OK, (await PdfAsync(A, idOfA)).StatusCode);
    }

    [Fact]
    public async Task EachCompanysPdfCarriesOnlyItsOwnFiscalData()
    {
        await SaveProfileAsync(A, LegalNameOfA, TaxIdOfA);
        await SaveProfileAsync(B, LegalNameOfB, TaxIdOfB, city: "Sevilla");
        var idOfA = await QuoteAsync(A, await CustomerAsync(A, "Cliente de A"));
        var idOfB = await QuoteAsync(B, await CustomerAsync(B, "Cliente de B"));

        var pdfOfA = await ReadPdfAsync(A, idOfA);
        var pdfOfB = await ReadPdfAsync(B, idOfB);

        Assert.Contains(LegalNameOfA, pdfOfA.Lines);
        Assert.Contains($"NIF {TaxIdOfA}", pdfOfA.Lines);
        Assert.DoesNotContain(LegalNameOfB, pdfOfA.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(TaxIdOfB, pdfOfA.Text, StringComparison.Ordinal);

        Assert.Contains(LegalNameOfB, pdfOfB.Lines);
        Assert.Contains($"NIF {TaxIdOfB}", pdfOfB.Lines);
        Assert.Contains("28009 Sevilla (Madrid)", pdfOfB.Lines);
        Assert.DoesNotContain(LegalNameOfA, pdfOfB.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(TaxIdOfA, pdfOfB.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATenantIdInTheQueryIsIgnored_TheCompanyComesFromTheTokenOnly()
    {
        await SaveProfileAsync(B, LegalNameOfB, TaxIdOfB);
        var idOfA = await QuoteAsync(A, await CustomerAsync(A, "Cliente de A"));

        var response = await PdfAsync(A, idOfA, query: $"?tenantId={fixture.B.TenantId}&companyId={fixture.B.TenantId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pdf = await PdfReading.OfAsync(response);

        Assert.Contains(LegalNameOfA, pdf.Lines);
        Assert.DoesNotContain(LegalNameOfB, pdf.Text, StringComparison.Ordinal);

        // Y B, aunque se haga pasar por la empresa de A en la consulta, sigue sin ver nada.
        var asB = await PdfAsync(B, idOfA, query: $"?tenantId={fixture.A.TenantId}");
        await AssertProblemAsync(asB, HttpStatusCode.NotFound, "quote.not_found");
    }

    // ---------- Texto hostil, tamaño y estabilidad ----------

    [Fact]
    public async Task HostileFreeText_IsPrintedAsTextAndNothingBreaks()
    {
        var emoji = char.ConvertFromUtf32(0x1F600);
        var customerId = await CustomerAsync(A, $"Cliente <b>negrita</b> {emoji}");
        var id = await QuoteAsync(A, customerId, title: "Reforma &amp; <script>alert(1)</script>", notes: "Línea uno\nLínea dos %s %d {0}\n\n\n\nLínea tres",
            items: [Line("<img src=x onerror=alert(1)> \\n \"comillas\" " + emoji, 1, 100), Line(new string('W', 300), 1, 100)]);

        var response = await PdfAsync(A, id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = (await PdfReading.OfAsync(response)).Text;
        var squashed = text.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);

        Assert.Contains("&amp;", text, StringComparison.Ordinal);
        Assert.Contains("<script>alert(1)</script>", squashed, StringComparison.Ordinal);
        Assert.Contains("<imgsrc=xonerror=alert(1)>", squashed, StringComparison.Ordinal);
        Assert.Contains("%s%d{0}", squashed, StringComparison.Ordinal);
        Assert.Contains(new string('W', 300), squashed, StringComparison.Ordinal);
        Assert.Contains("negrita</b>?", squashed, StringComparison.Ordinal);
        Assert.DoesNotContain(emoji, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AQuoteOfHundredLines_GivesSeveralPagesInOrder_WithTheTotalsOnce()
    {
        var items = Enumerable.Range(1, 100).Select(i => Line($"Partida {i}", 1, 1000 + i)).ToArray();
        var id = await QuoteAsync(A, await CustomerAsync(A, "Cliente de cien líneas"), items: items);

        var pdf = await ReadPdfAsync(A, id);

        Assert.True(pdf.PageCount >= 3);
        var numbers = pdf.Lines.Where(l => l.StartsWith("Partida ", StringComparison.Ordinal)).Select(l => int.Parse(l.Split(' ')[1], CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(Enumerable.Range(1, 100), numbers);
        Assert.Single(pdf.Pages.Where(p => p.Contains("TOTAL", StringComparison.Ordinal)));
        Assert.Contains("Página 1 de " + pdf.PageCount, pdf.Pages[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoDownloadsOfTheSameQuote_HaveTheSameContent()
    {
        var id = await QuoteAsync(A, await CustomerAsync(A, "Cliente estable"));

        var first = await ReadPdfAsync(A, id);
        var second = await ReadPdfAsync(A, id);

        Assert.Equal(first.Pages, second.Pages);
    }

    [Fact]
    public async Task DownloadingThePdf_ChangesNothingInTheQuote()
    {
        var id = await QuoteAsync(A, await CustomerAsync(A, "Cliente intacto"), send: false);
        var before = (await A.GetStringAsync($"/api/v1/quotes/{id}"));

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await PdfAsync(A, id)).StatusCode);
        }

        // La misma lectura, con la misma versión: descargar el PDF no es una modificación (y un borrador sigue pudiendo editarse con la versión de antes).
        Assert.Equal(before, await A.GetStringAsync($"/api/v1/quotes/{id}"));
        var version = JsonDocument.Parse(before).RootElement.GetProperty("version").GetString();
        var edit = await A.PutAsJsonAsync($"/api/v1/quotes/{id}", new { version, title = "Editado después", items = new[] { Line("Otra", 1, 200) } });
        Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);
    }

    [Fact]
    public async Task SeveralDownloadsAtTheSameTime_AllGiveTheirOwnCorrectDocument()
    {
        var ids = new List<(Guid Id, string Title)>();
        for (var i = 0; i < 8; i++)
        {
            var title = $"Presupuesto simultáneo {i}";
            ids.Add((await QuoteAsync(A, await CustomerAsync(A, $"Cliente simultáneo {i}"), title: title), title));
        }

        var downloads = await Task.WhenAll(ids.Select(async x => (x.Title, Pdf: await ReadPdfAsync(A, x.Id))));

        Assert.All(downloads, d =>
        {
            Assert.Contains(d.Title, d.Pdf.Lines);
            // Ningún documento lleva el título de otro.
            Assert.DoesNotContain(downloads.Where(o => o.Title != d.Title), o => d.Pdf.Text.Contains(o.Title, StringComparison.Ordinal));
        });
    }

    // ---------- El límite de peticiones (R14) ----------

    [Fact]
    public async Task TheThirtyFirstDownloadFromOneIpInAMinute_IsRejectedWith429_AndOtherIpsAndOtherRoutesAreNot()
    {
        var id = await QuoteAsync(A, await CustomerAsync(A, "Cliente del límite"), items: [Line("Una sola línea", 1, 100)]);
        const string ip = "10.99.88.77";

        for (var attempt = 1; attempt <= 30; attempt++)
        {
            var allowed = await PdfAsync(A, id, ip);
            Assert.True(allowed.StatusCode == HttpStatusCode.OK, $"La descarga {attempt} dio {(int)allowed.StatusCode}.");
        }

        var rejected = await PdfAsync(A, id, ip);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.True(rejected.Headers.Contains("Retry-After"));
        Assert.Equal("rate_limit.exceeded", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // Controles positivos: otra IP descarga; y la misma IP agotada sigue pudiendo leer el presupuesto (el cubo es solo de la descarga del PDF).
        Assert.Equal(HttpStatusCode.OK, (await PdfAsync(A, id, "10.99.88.78")).StatusCode);
        var read = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/quotes/{id}");
        read.Headers.Add(TestClientIpStartupFilter.HeaderName, ip);
        Assert.Equal(HttpStatusCode.OK, (await A.SendAsync(read)).StatusCode);
    }

    // ---------- El registro: nada del documento ----------

    [Fact]
    public async Task NothingOfTheDocumentReachesTheLogs_NotEvenWhenTheDownloadFails()
    {
        var marker = $"reservado{Guid.NewGuid():N}";
        await SaveProfileAsync(A, $"Nombre fiscal {marker}", "12345678Z");
        var customerId = await CustomerAsync(A, $"Cliente {marker}", address: $"Calle {marker}");
        var id = await QuoteAsync(A, customerId, title: $"Título {marker}", notes: $"Notas {marker}", items: [Line($"Descripción {marker}", 1, 987654321)]);
        var before = fixture.Factory.Logs.Entries.Count;

        await ReadPdfAsync(A, id);
        await PdfAsync(A, Guid.NewGuid());                // un 404
        await ResetProfileAsync(fixture.A.TenantId);
        await PdfAsync(A, id);                            // un 400
        await SaveProfileAsync(A, LegalNameOfA, TaxIdOfA);

        var entries = fixture.Factory.Logs.Entries.Skip(before).ToList();
        foreach (var secret in new[] { marker, "12345678Z", "9.876.543,21", "987654321", "98765432" })
        {
            Assert.DoesNotContain(entries, e => e.Message.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }

        Assert.DoesNotContain(entries, e => e.EventId == SecurityEventIds.UnhandledException);
    }
}
