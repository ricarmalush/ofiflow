using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OfiFlow.Api.Tests.Errors;
using OfiFlow.Application.Common.Logging;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// Los presupuestos de principio a fin por HTTP (spec 023, bloque 2), con la API real, SQL Server real y dos empresas registradas de verdad: el flujo completo, que el
/// servidor es la única fuente del dinero, cada error en su código, la inmutabilidad del presupuesto enviado, la concurrencia, la paginación y que una empresa no toca
/// los presupuestos de otra. Cada negativa va con su control positivo.
/// </summary>
public class QuoteEndToEndTests(TwoTenantsFixture fixture) : IClassFixture<TwoTenantsFixture>
{
    private HttpClient A => fixture.A.Client;

    private HttpClient B => fixture.B.Client;

    private static object Line(object quantity, long price, int vat = 21, string description = "Mano de obra") =>
        new { description, quantity, unitPriceCents = price, vatPercent = vat };

    private static object[] TwoLines() => [Line(2.5m, 1234), Line(1, 5000, 0, "Material")];

    private Task<HttpResponseMessage> CreateRawAsync(HttpClient client, Guid customerId, object[]? items = null, Guid? jobId = null, object? extra = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["customerId"] = customerId,
            ["jobId"] = jobId,
            ["title"] = "Reforma del baño",
            ["notes"] = "Pago a 30 días",
            ["validUntil"] = "2026-12-31",
            ["items"] = items ?? TwoLines()
        };

        if (extra is not null)
        {
            foreach (var property in extra.GetType().GetProperties())
            {
                body[property.Name] = property.GetValue(extra);
            }
        }

        return client.PostAsJsonAsync("/api/v1/quotes", body);
    }

    private async Task<Guid> CreateAsync(HttpClient client, Guid customerId, object[]? items = null)
    {
        var response = await CreateRawAsync(client, customerId, items);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/v1/quotes/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }

    private static Task<HttpResponseMessage> UpdateAsync(HttpClient client, Guid id, string version, string title = "Título nuevo", object[]? items = null, Guid? jobId = null) =>
        client.PutAsJsonAsync($"/api/v1/quotes/{id}", new { version, jobId, title, notes = (string?)null, validUntil = (string?)null, items = items ?? [Line(2, 1000, 10)] });

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var problem = await Dictionary.ReadProblemAsync(response);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal(Dictionary.Spanish(code), problem.GetProperty("detail").GetString());
    }

    private async Task<Guid> CustomerOfBAsync() => await TwoTenantsFixture.CreateCustomerAsync(B, $"Cliente de B {Guid.NewGuid():N}");

    // ---------- El flujo completo ----------

    [Fact]
    public async Task TheWholeFlow_CreateEditSendAccept_AndEveryStepIsReadBack()
    {
        var id = await CreateAsync(A, fixture.CustomerIdOfA);
        var draft = await GetAsync(A, id);
        Assert.Equal("Draft", draft.GetProperty("status").GetString());
        Assert.Equal(TwoTenantsFixture.CustomerNameOfA, draft.GetProperty("customerName").GetString());
        Assert.Equal("2026-12-31", draft.GetProperty("validUntil").GetString());

        var edit = await UpdateAsync(A, id, draft.GetProperty("version").GetString()!, "Reforma completa", [Line(1, 10000), Line(2, 500, 10, "Tubo")]);
        Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);
        var edited = await GetAsync(A, id);
        Assert.Equal("Reforma completa", edited.GetProperty("title").GetString());
        Assert.Equal(2, edited.GetProperty("items").GetArrayLength());
        Assert.NotEqual(draft.GetProperty("version").GetString(), edited.GetProperty("version").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await A.PostAsync($"/api/v1/quotes/{id}/send", null)).StatusCode);
        var sent = await GetAsync(A, id);
        Assert.Equal("Sent", sent.GetProperty("status").GetString());
        Assert.EndsWith("Z", sent.GetProperty("sentAt").GetString());
        Assert.Equal(JsonValueKind.Null, sent.GetProperty("decidedAt").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await A.PostAsync($"/api/v1/quotes/{id}/accept", null)).StatusCode);
        var accepted = await GetAsync(A, id);
        Assert.Equal("Accepted", accepted.GetProperty("status").GetString());
        Assert.EndsWith("Z", accepted.GetProperty("decidedAt").GetString());
    }

    [Fact]
    public async Task ACreatedQuote_HasALocationAndTheNextNumber_AndTheReadsAreNotCacheable()
    {
        var first = await CreateRawAsync(A, fixture.CustomerIdOfA);
        var second = await CreateRawAsync(A, fixture.CustomerIdOfA);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.EndsWith($"/api/v1/quotes/{firstId}", first.Headers.Location!.ToString());
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal((await GetAsync(A, firstId)).GetProperty("number").GetInt32() + 1, (await GetAsync(A, secondId)).GetProperty("number").GetInt32());

        var read = await A.GetAsync($"/api/v1/quotes/{firstId}");
        var list = await A.GetAsync("/api/v1/quotes");
        Assert.Contains("no-store", read.Headers.CacheControl!.ToString());
        Assert.Contains("no-store", list.Headers.CacheControl!.ToString());
    }

    // ---------- El dinero lo pone el servidor ----------

    [Fact]
    public async Task TheServer_ComputesTheTotals_AndIgnoresAnyTotalNumberStatusOrCompanyTheRequestTriesToSet()
    {
        var response = await CreateRawAsync(A, fixture.CustomerIdOfA, extra: new
        {
            totalCents = 1,
            netCents = 1,
            vatCents = 1,
            number = 999,
            status = "Accepted",
            tenantId = fixture.B.TenantId,
            sentAt = "2020-01-01T00:00:00Z"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var quote = await GetAsync(A, id);

        Assert.Equal(3085 + 5000, quote.GetProperty("netCents").GetInt64());
        Assert.Equal(648, quote.GetProperty("vatCents").GetInt64());
        Assert.Equal(3085 + 5000 + 648, quote.GetProperty("totalCents").GetInt64());
        Assert.NotEqual(999, quote.GetProperty("number").GetInt32());
        Assert.Equal("Draft", quote.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, quote.GetProperty("sentAt").ValueKind);
        // Y no es de la empresa B por mucho que la petición lo dijera.
        Assert.Equal(HttpStatusCode.NotFound, (await B.GetAsync($"/api/v1/quotes/{id}")).StatusCode);
    }

    [Fact]
    public async Task TheDetail_ShowsEachLinesNet_TheVatBreakdownAndTheTotals_AsInTheFixedCases()
    {
        var id = await CreateAsync(A, fixture.CustomerIdOfA, [
            Line(1, 10000, 21, "C"),
            Line(3, 2500, 10, "A"),
            Line(2, 1999, 4, "B"),
            Line(1, 5000, 0, "D")]);

        var quote = await GetAsync(A, id);

        Assert.Equal(["C", "A", "B", "D"], quote.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("description").GetString()));
        Assert.Equal([10000L, 7500L, 3998L, 5000L], quote.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("netCents").GetInt64()));
        Assert.Equal([(0, 5000L, 0L), (4, 3998L, 160L), (10, 7500L, 750L), (21, 10000L, 2100L)],
            quote.GetProperty("vatBreakdown").EnumerateArray().Select(b => (b.GetProperty("vatPercent").GetInt32(), b.GetProperty("baseCents").GetInt64(), b.GetProperty("vatCents").GetInt64())));
        Assert.Equal(26498, quote.GetProperty("netCents").GetInt64());
        Assert.Equal(3010, quote.GetProperty("vatCents").GetInt64());
        Assert.Equal(29508, quote.GetProperty("totalCents").GetInt64());
    }

    // ---------- Validación ----------

    private async Task AssertFieldErrorAsync(HttpResponseMessage response, string fieldContains, string code)
    {
        var problem = await Dictionary.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        Assert.Contains(problem.GetProperty("errors").EnumerateArray(), e =>
            e.GetProperty("field").GetString()!.Contains(fieldContains, StringComparison.Ordinal) && e.GetProperty("code").GetString() == code);
    }

    [Fact]
    public async Task AVatRateOutsideTheList_IsAFieldError_WithItsOwnCode_AndCreatesNothing()
    {
        var before = (await (await A.GetAsync("/api/v1/quotes")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("totalCount").GetInt32();

        var response = await CreateRawAsync(A, fixture.CustomerIdOfA, [Line(1, 1000, 13)]);

        await AssertFieldErrorAsync(response, "VatPercent", "quote.vat_invalid");
        Assert.Equal(before, (await (await A.GetAsync("/api/v1/quotes")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("0.0001")]
    [InlineData("100000")]
    public async Task AnInvalidQuantity_IsAFieldError_WithItsOwnCode(string quantity)
    {
        var line = new Dictionary<string, object> { ["description"] = "x", ["quantity"] = JsonDocument.Parse(quantity).RootElement.Clone(), ["unitPriceCents"] = 100, ["vatPercent"] = 21 };

        var response = await CreateRawAsync(A, fixture.CustomerIdOfA, [line]);

        await AssertFieldErrorAsync(response, "Quantity", "quote.quantity_invalid");
    }

    [Fact]
    public async Task AValidUntilOutsideTheRange_IsAFieldError_WithItsOwnCode()
    {
        var response = await CreateRawAsync(A, fixture.CustomerIdOfA, extra: new { validUntil = "2100-01-01" });

        await AssertFieldErrorAsync(response, "ValidUntil", "quote.valid_until_invalid");
    }

    [Fact]
    public async Task APastValidUntil_IsAccepted()
    {
        var response = await CreateRawAsync(A, fixture.CustomerIdOfA, extra: new { validUntil = "2001-05-05" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task TooManyLinesAnEmptyTitleAndAnInsanePrice_AreFieldErrors()
    {
        await AssertFieldErrorAsync(await CreateRawAsync(A, fixture.CustomerIdOfA, Enumerable.Repeat(Line(1, 100), 101).ToArray()), "Items", "PredicateValidator");
        await AssertFieldErrorAsync(await CreateRawAsync(A, fixture.CustomerIdOfA, extra: new { title = "  " }), "Title", "NotEmptyValidator");
        await AssertFieldErrorAsync(await CreateRawAsync(A, fixture.CustomerIdOfA, [Line(1, 10_000_000_000)]), "UnitPriceCents", "InclusiveBetweenValidator");
        await AssertFieldErrorAsync(await CreateRawAsync(A, fixture.CustomerIdOfA, [Line(1, -1)]), "UnitPriceCents", "InclusiveBetweenValidator");
        Assert.Equal(HttpStatusCode.Created, (await CreateRawAsync(A, fixture.CustomerIdOfA, Enumerable.Repeat(Line(1, 100), 100).ToArray())).StatusCode);
    }

    // ---------- Aislamiento ----------

    [Fact]
    public async Task ACustomerOfAnotherCompany_IsTheSameNotFoundAsOneThatDoesNotExist()
    {
        var customerOfB = await CustomerOfBAsync();

        await AssertProblemAsync(await CreateRawAsync(A, customerOfB), HttpStatusCode.NotFound, "customer.not_found");
        await AssertProblemAsync(await CreateRawAsync(A, Guid.NewGuid()), HttpStatusCode.NotFound, "customer.not_found");
        Assert.Equal(HttpStatusCode.Created, (await CreateRawAsync(B, customerOfB)).StatusCode); // control positivo: es válido para su propia empresa
    }

    [Fact]
    public async Task AJobOfAnotherCompanyOrOfAnotherCustomer_IsRefused_AndOneOfTheSameCustomerLinks()
    {
        var otherCustomerOfA = await TwoTenantsFixture.CreateCustomerAsync(A, $"Otro cliente {Guid.NewGuid():N}");
        var jobOfOtherCustomer = await TwoTenantsFixture.CreateJobAsync(A, otherCustomerOfA, "De otro cliente");
        var jobOfB = await TwoTenantsFixture.CreateJobAsync(B, await CustomerOfBAsync(), "De B");

        await AssertProblemAsync(await CreateRawAsync(A, fixture.CustomerIdOfA, jobId: jobOfOtherCustomer), HttpStatusCode.BadRequest, "quote.job_customer_mismatch");
        await AssertProblemAsync(await CreateRawAsync(A, fixture.CustomerIdOfA, jobId: jobOfB), HttpStatusCode.NotFound, "job.not_found");
        await AssertProblemAsync(await CreateRawAsync(A, fixture.CustomerIdOfA, jobId: Guid.NewGuid()), HttpStatusCode.NotFound, "job.not_found");

        var linked = await CreateRawAsync(A, fixture.CustomerIdOfA, jobId: fixture.JobIdOfA);
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);
        var id = (await linked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(fixture.JobIdOfA, (await GetAsync(A, id)).GetProperty("jobId").GetGuid());
    }

    [Fact]
    public async Task AnotherCompany_CannotReadChangeSendDecideOrDeleteMyQuote_AndNothingChanged()
    {
        var id = await CreateAsync(A, fixture.CustomerIdOfA);
        var version = (await GetAsync(A, id)).GetProperty("version").GetString()!;

        await AssertProblemAsync(await B.GetAsync($"/api/v1/quotes/{id}"), HttpStatusCode.NotFound, "quote.not_found");
        await AssertProblemAsync(await UpdateAsync(B, id, version, "Robado"), HttpStatusCode.NotFound, "quote.not_found");
        await AssertProblemAsync(await B.DeleteAsync($"/api/v1/quotes/{id}"), HttpStatusCode.NotFound, "quote.not_found");
        await AssertProblemAsync(await B.PostAsync($"/api/v1/quotes/{id}/send", null), HttpStatusCode.NotFound, "quote.not_found");
        await AssertProblemAsync(await B.PostAsync($"/api/v1/quotes/{id}/accept", null), HttpStatusCode.NotFound, "quote.not_found");
        await AssertProblemAsync(await B.PostAsync($"/api/v1/quotes/{id}/reject", null), HttpStatusCode.NotFound, "quote.not_found");
        await AssertProblemAsync(await B.GetAsync($"/api/v1/quotes/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "quote.not_found"); // y es el mismo que uno inexistente

        var untouched = await GetAsync(A, id);
        Assert.Equal("Draft", untouched.GetProperty("status").GetString());
        Assert.Equal("Reforma del baño", untouched.GetProperty("title").GetString());
        Assert.Equal(version, untouched.GetProperty("version").GetString());
    }

    [Fact]
    public async Task TheNumbersOfEachCompany_AreIndependent_AndTheListingsDoNotMix()
    {
        var customerOfB = await CustomerOfBAsync();
        var idB = await CreateAsync(B, customerOfB);
        await CreateAsync(A, fixture.CustomerIdOfA);

        var listB = (await (await B.GetAsync("/api/v1/quotes?pageSize=100")).Content.ReadFromJsonAsync<JsonElement>()).Clone();
        var numbersB = listB.GetProperty("items").EnumerateArray().Select(q => q.GetProperty("number").GetInt32()).Order().ToList();

        Assert.Equal(Enumerable.Range(1, numbersB.Count), numbersB); // 1, 2, 3...: sin saltos ni números de la empresa A
        Assert.Contains(listB.GetProperty("items").EnumerateArray(), q => q.GetProperty("id").GetGuid() == idB);
        Assert.All(listB.GetProperty("items").EnumerateArray(), q => Assert.NotEqual(fixture.CustomerIdOfA, q.GetProperty("customerId").GetGuid()));
    }

    // ---------- Estados e inmutabilidad ----------

    [Fact]
    public async Task ASentQuote_CannotBeEditedNorDeleted_AndTheDraftControlCan()
    {
        var sentId = await CreateAsync(A, fixture.CustomerIdOfA);
        var draftId = await CreateAsync(A, fixture.CustomerIdOfA);
        await A.PostAsync($"/api/v1/quotes/{sentId}/send", null);
        var version = (await GetAsync(A, sentId)).GetProperty("version").GetString()!;

        await AssertProblemAsync(await UpdateAsync(A, sentId, version, "Reescrito", [Line(1, 1)]), HttpStatusCode.BadRequest, "quote.not_editable");
        await AssertProblemAsync(await A.DeleteAsync($"/api/v1/quotes/{sentId}"), HttpStatusCode.BadRequest, "quote.cannot_delete");
        var still = await GetAsync(A, sentId);
        Assert.Equal("Reforma del baño", still.GetProperty("title").GetString());
        Assert.Equal(3085 + 5000 + 648, still.GetProperty("totalCents").GetInt64());

        // Control positivo: el borrador sí se elimina, y desaparece.
        Assert.Equal(HttpStatusCode.NoContent, (await A.DeleteAsync($"/api/v1/quotes/{draftId}")).StatusCode);
        await AssertProblemAsync(await A.GetAsync($"/api/v1/quotes/{draftId}"), HttpStatusCode.NotFound, "quote.not_found");
    }

    [Fact]
    public async Task SendingWithoutLines_IsRefused_AndWithLines_Works()
    {
        var emptyId = await CreateAsync(A, fixture.CustomerIdOfA, []);
        var fullId = await CreateAsync(A, fixture.CustomerIdOfA);

        await AssertProblemAsync(await A.PostAsync($"/api/v1/quotes/{emptyId}/send", null), HttpStatusCode.BadRequest, "quote.empty");
        Assert.Equal("Draft", (await GetAsync(A, emptyId)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await A.PostAsync($"/api/v1/quotes/{fullId}/send", null)).StatusCode);
    }

    [Fact]
    public async Task EveryTransitionFromAStateThatIsNotItsOwn_IsRefused_WithItsCode()
    {
        var draft = await CreateAsync(A, fixture.CustomerIdOfA);
        var sent = await CreateAsync(A, fixture.CustomerIdOfA);
        var accepted = await CreateAsync(A, fixture.CustomerIdOfA);
        var rejected = await CreateAsync(A, fixture.CustomerIdOfA);
        foreach (var id in new[] { sent, accepted, rejected })
        {
            await A.PostAsync($"/api/v1/quotes/{id}/send", null);
        }

        await A.PostAsync($"/api/v1/quotes/{accepted}/accept", null);
        await A.PostAsync($"/api/v1/quotes/{rejected}/reject", null);

        await AssertProblemAsync(await A.PostAsync($"/api/v1/quotes/{draft}/accept", null), HttpStatusCode.BadRequest, "quote.cannot_accept");
        await AssertProblemAsync(await A.PostAsync($"/api/v1/quotes/{draft}/reject", null), HttpStatusCode.BadRequest, "quote.cannot_reject");
        await AssertProblemAsync(await A.PostAsync($"/api/v1/quotes/{sent}/send", null), HttpStatusCode.BadRequest, "quote.cannot_send");
        await AssertProblemAsync(await A.PostAsync($"/api/v1/quotes/{accepted}/send", null), HttpStatusCode.BadRequest, "quote.cannot_send");
        await AssertProblemAsync(await A.PostAsync($"/api/v1/quotes/{accepted}/accept", null), HttpStatusCode.BadRequest, "quote.cannot_accept");
        await AssertProblemAsync(await A.PostAsync($"/api/v1/quotes/{accepted}/reject", null), HttpStatusCode.BadRequest, "quote.cannot_reject");
        await AssertProblemAsync(await A.PostAsync($"/api/v1/quotes/{rejected}/accept", null), HttpStatusCode.BadRequest, "quote.cannot_accept");
        await AssertProblemAsync(await A.PostAsync($"/api/v1/quotes/{rejected}/reject", null), HttpStatusCode.BadRequest, "quote.cannot_reject");

        // Controles positivos: lo que SÍ se puede desde su estado.
        Assert.Equal("Draft", (await GetAsync(A, draft)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await A.PostAsync($"/api/v1/quotes/{sent}/reject", null)).StatusCode);
        Assert.Equal("Accepted", (await GetAsync(A, accepted)).GetProperty("status").GetString());
        Assert.Equal("Rejected", (await GetAsync(A, rejected)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task TheEdit_ReplacesTheWholeHeader_JobNotesAndValidityIncluded_AndCanUnlinkTheJob()
    {
        var id = await CreateAsync(A, fixture.CustomerIdOfA);
        var version = (await GetAsync(A, id)).GetProperty("version").GetString()!;

        var edit = await A.PutAsJsonAsync($"/api/v1/quotes/{id}", new
        {
            version,
            jobId = fixture.JobIdOfA,
            title = "Cabecera nueva",
            notes = "Notas nuevas",
            validUntil = "2027-03-15",
            items = new[] { Line(1, 100) }
        });
        Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);

        var edited = await GetAsync(A, id);
        Assert.Equal(fixture.JobIdOfA, edited.GetProperty("jobId").GetGuid());
        Assert.Equal("Cabecera nueva", edited.GetProperty("title").GetString());
        Assert.Equal("Notas nuevas", edited.GetProperty("notes").GetString());
        Assert.Equal("2027-03-15", edited.GetProperty("validUntil").GetString());

        // Sin trabajo, sin notas y sin validez: la sustitución es total.
        var clear = await A.PutAsJsonAsync($"/api/v1/quotes/{id}", new { version = edited.GetProperty("version").GetString(), jobId = (Guid?)null, title = "Sin nada", notes = (string?)null, validUntil = (string?)null, items = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        var cleared = await GetAsync(A, id);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("jobId").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("notes").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("validUntil").ValueKind);
        Assert.Equal(0, cleared.GetProperty("items").GetArrayLength());
        Assert.Equal(0, cleared.GetProperty("totalCents").GetInt64());
    }

    // ---------- Concurrencia ----------

    [Fact]
    public async Task EditingWithAStaleVersion_IsRefused_AndNothingIsOverwritten()
    {
        var id = await CreateAsync(A, fixture.CustomerIdOfA);
        var original = (await GetAsync(A, id)).GetProperty("version").GetString()!;

        Assert.Equal(HttpStatusCode.NoContent, (await UpdateAsync(A, id, original, "Primera edición")).StatusCode);
        await AssertProblemAsync(await UpdateAsync(A, id, original, "Segunda, con la versión vieja"), HttpStatusCode.BadRequest, "quote.changed_meanwhile");

        var current = await GetAsync(A, id);
        Assert.Equal("Primera edición", current.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await UpdateAsync(A, id, current.GetProperty("version").GetString()!, "Tercera")).StatusCode); // control positivo
    }

    [Fact]
    public async Task TwoSimultaneousEditionsWithTheSameVersion_OneWins_NeverA500()
    {
        for (var round = 0; round < 5; round++)
        {
            var id = await CreateAsync(A, fixture.CustomerIdOfA);
            var version = (await GetAsync(A, id)).GetProperty("version").GetString()!;

            var responses = await Task.WhenAll(
                UpdateAsync(A, id, version, "Gana uno", [Line(1, 111)]),
                UpdateAsync(A, id, version, "Gana otro", [Line(1, 222), Line(1, 333)]));

            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.NoContent));
            var loser = responses.Single(r => r.StatusCode != HttpStatusCode.NoContent);
            await AssertProblemAsync(loser, HttpStatusCode.BadRequest, "quote.changed_meanwhile");
            var stored = await GetAsync(A, id);
            Assert.Contains(stored.GetProperty("title").GetString(), new[] { "Gana uno", "Gana otro" });
        }
    }

    [Fact]
    public async Task EditingAndSendingAtTheSameTime_LeaveAStateConsistentWithWhatEachOneReported_NorA500()
    {
        for (var round = 0; round < 8; round++)
        {
            var id = await CreateAsync(A, fixture.CustomerIdOfA, [Line(1, 10000)]);
            var version = (await GetAsync(A, id)).GetProperty("version").GetString()!;

            var responses = await Task.WhenAll(
                UpdateAsync(A, id, version, "Reescrito", [Line(1, 1)]),
                A.PostAsync($"/api/v1/quotes/{id}/send", null));

            Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
            var (editResponse, sendResponse) = (responses[0], responses[1]);
            var edited = editResponse.StatusCode == HttpStatusCode.NoContent;
            var sent = sendResponse.StatusCode == HttpStatusCode.NoContent;
            var stored = await GetAsync(A, id);

            // El estado final tiene que ser COHERENTE con lo que respondió la API a cada una (el resultado depende de quién llegó antes):
            //   edición 204 y envío 204 -> la edición fue primero y se envió el presupuesto YA editado (Sent, con las líneas nuevas);
            //   edición 204 y envío rechazado -> sigue siendo un borrador con las líneas nuevas;
            //   edición rechazada y envío 204 -> el envío fue primero: Sent con las líneas ORIGINALES (la edición no se coló);
            //   las dos rechazadas -> un borrador intacto (no puede ocurrir con estas dos, pero no se da por supuesto).
            Assert.Equal(sent ? "Sent" : "Draft", stored.GetProperty("status").GetString());
            Assert.Equal(edited ? 1 : 10000, stored.GetProperty("items")[0].GetProperty("unitPriceCents").GetInt64());
            Assert.Equal(edited ? "Reescrito" : "Reforma del baño", stored.GetProperty("title").GetString());
            if (!edited)
            {
                var problem = await Dictionary.ReadProblemAsync(editResponse);
                Assert.Contains(problem.GetProperty("code").GetString(), new[] { "quote.changed_meanwhile", "quote.not_editable" });
            }
        }
    }

    [Fact]
    public async Task SeveralSimultaneousCreations_GetDifferentNumbers_NeverA500()
    {
        var customerId = await TwoTenantsFixture.CreateCustomerAsync(A, $"Cliente de carrera {Guid.NewGuid():N}");

        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => CreateRawAsync(A, customerId)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid()));
        var numbers = new List<int>();
        foreach (var id in ids)
        {
            numbers.Add((await GetAsync(A, id)).GetProperty("number").GetInt32());
        }

        Assert.Equal(3, numbers.Distinct().Count());
    }

    // ---------- Listado ----------

    [Fact]
    public async Task TheListing_IsPaged_Filtered_AndSendsNoLines()
    {
        var customerId = await TwoTenantsFixture.CreateCustomerAsync(A, $"Cliente del listado {Guid.NewGuid():N}");
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await CreateAsync(A, customerId));
            await Task.Delay(20);
        }

        await A.PostAsync($"/api/v1/quotes/{ids[1]}/send", null);

        var page = (await (await A.GetAsync($"/api/v1/quotes?customerId={customerId}&pageSize=2&page=2")).Content.ReadFromJsonAsync<JsonElement>()).Clone();
        Assert.Equal(5, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, page.GetProperty("page").GetInt32());
        Assert.Equal(2, page.GetProperty("pageSize").GetInt32());
        Assert.Equal([ids[2], ids[1]], page.GetProperty("items").EnumerateArray().Select(q => q.GetProperty("id").GetGuid())); // del más reciente al más antiguo
        Assert.All(page.GetProperty("items").EnumerateArray(), q =>
        {
            Assert.False(q.TryGetProperty("items", out _)); // sin las líneas
            Assert.True(q.TryGetProperty("customerName", out _));
        });

        var sent = (await (await A.GetAsync($"/api/v1/quotes?customerId={customerId}&status=Sent")).Content.ReadFromJsonAsync<JsonElement>()).Clone();
        Assert.Equal([ids[1]], sent.GetProperty("items").EnumerateArray().Select(q => q.GetProperty("id").GetGuid()));
    }

    [Theory]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=101")]
    [InlineData("?page=0")]
    [InlineData("?status=Inexistente")]
    [InlineData("?customerId=no-es-un-guid")]
    public async Task AnInvalidPageOrFilter_Is400_NeverA500(string query)
    {
        var response = await A.GetAsync($"/api/v1/quotes{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task APageOfOneHundred_IsAccepted()
    {
        Assert.Equal(HttpStatusCode.OK, (await A.GetAsync("/api/v1/quotes?pageSize=100")).StatusCode);
    }

    // ---------- Registros ----------

    [Fact]
    public async Task NoTitleNoteDescriptionNorAmount_ReachesTheLogs()
    {
        var marker = $"secreto-{Guid.NewGuid():N}";
        var before = fixture.Factory.Logs.Entries.Count;

        var response = await A.PostAsJsonAsync("/api/v1/quotes", new
        {
            customerId = fixture.CustomerIdOfA,
            title = $"Título {marker}",
            notes = $"Notas {marker}",
            validUntil = "2026-12-31",
            items = new[] { Line(7.777m, 7_654_321, 21, $"Línea {marker}") }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var version = (await GetAsync(A, id)).GetProperty("version").GetString()!;
        await UpdateAsync(A, id, version, $"Otro {marker}", [Line(1, 7_654_321, 99, marker)]); // un 400 de validación: también sin rastro
        await A.PostAsync($"/api/v1/quotes/{id}/send", null);

        var entries = fixture.Factory.Logs.Entries.Skip(before).ToList();
        Assert.DoesNotContain(entries, e => e.Message.Contains(marker, StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.Message.Contains("7654321", StringComparison.Ordinal) || e.Message.Contains("7.777", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.EventId == SecurityEventIds.UnhandledException);
    }

    // ---------- Sin sesión ----------

    [Fact]
    public async Task WithoutASession_EveryRouteIs401()
    {
        var anonymous = fixture.Anonymous;
        var id = Guid.NewGuid();

        var responses = new[]
        {
            await anonymous.GetAsync("/api/v1/quotes"),
            await anonymous.GetAsync($"/api/v1/quotes/{id}"),
            await anonymous.PostAsJsonAsync("/api/v1/quotes", new { }),
            await anonymous.PutAsJsonAsync($"/api/v1/quotes/{id}", new { }),
            await anonymous.DeleteAsync($"/api/v1/quotes/{id}"),
            await anonymous.PostAsync($"/api/v1/quotes/{id}/send", null),
            await anonymous.PostAsync($"/api/v1/quotes/{id}/accept", null),
            await anonymous.PostAsync($"/api/v1/quotes/{id}/reject", null)
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
    }
}
