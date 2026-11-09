using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Api.Tests.Errors;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// El perfil fiscal de principio a fin por HTTP (spec 024, bloque 2), con la API real, SQL Server real y dos empresas registradas de verdad: el alta y la lectura normalizada, cada error en su
/// código, que la empresa sale solo del token, el aislamiento entre empresas, la carrera de dos altas y que ningún dato del perfil llega al registro. Cada negativa va con su control positivo.
/// </summary>
public class CompanyProfileEndToEndTests(TwoTenantsFixture fixture) : IClassFixture<TwoTenantsFixture>
{
    private const string Route = "/api/v1/company/profile";

    private HttpClient A => fixture.A.Client;

    private HttpClient B => fixture.B.Client;

    private static Dictionary<string, object?> Body(string legalName = "Marta Gil Pérez", string taxId = "12345678Z", string addressLine = "Calle Mayor 1, 2.º B", string postalCode = "28001", string city = "Madrid",
        string? province = "Madrid", string? phone = "600123456", string? email = "marta@example.com") =>
        new()
        {
            ["legalName"] = legalName,
            ["taxId"] = taxId,
            ["addressLine"] = addressLine,
            ["postalCode"] = postalCode,
            ["city"] = city,
            ["province"] = province,
            ["phone"] = phone,
            ["email"] = email
        };

    private static async Task<JsonElement> ReadAsync(HttpClient client)
    {
        var response = await client.GetAsync(Route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }

    private static async Task PutOkAsync(HttpClient client, Dictionary<string, object?> body) =>
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(Route, body)).StatusCode);

    private async Task ResetAsync(Guid tenantId)
    {
        // El perfil nace al guardarlo: para probar «una empresa que todavía no lo tiene» se borra su fila directamente (no hay operación de borrado en la API).
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CompanyProfiles.IgnoreQueryFilters().Where(p => p.TenantId == tenantId).ExecuteDeleteAsync();
    }

    private async Task<int> RowsAsync(Guid tenantId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.CompanyProfiles.IgnoreQueryFilters().CountAsync(p => p.TenantId == tenantId);
    }

    private static async Task AssertFieldErrorAsync(HttpResponseMessage response, string field, string? code = null)
    {
        var problem = await Dictionary.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        Assert.Contains(problem.GetProperty("errors").EnumerateArray(), e =>
            string.Equals(e.GetProperty("field").GetString(), field, StringComparison.OrdinalIgnoreCase) && (code is null || e.GetProperty("code").GetString() == code));
    }

    // ---------- Lectura de una empresa sin perfil ----------

    [Fact]
    public async Task ACompanyWithoutProfile_ReadsAnEmptyOne_NotAnError_AndItIsNeverCached()
    {
        await ResetAsync(fixture.A.TenantId);

        var response = await A.GetAsync(Route);
        var profile = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(profile.GetProperty("isComplete").GetBoolean());
        foreach (var field in new[] { "legalName", "taxId", "addressLine", "postalCode", "city", "province", "phone", "email" })
        {
            Assert.Equal(JsonValueKind.Null, profile.GetProperty(field).ValueKind);
        }
    }

    // ---------- Guardar y leer ----------

    [Fact]
    public async Task TheFirstSave_Is204_AndTheProfileIsReadBackNormalized_Complete_AndNotCached()
    {
        await ResetAsync(fixture.A.TenantId);

        var save = await A.PutAsJsonAsync(Route, Body(legalName: "  Marta Gil Pérez ", taxId: " 12.345.678-z ", addressLine: " Calle Mayor 1 ", postalCode: " 28001 ", city: " Madrid ", province: "  ", phone: " 600123456 ", email: " marta@example.com "));
        var read = await A.GetAsync(Route);
        var profile = await read.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);
        Assert.Equal("no-store", read.Headers.CacheControl?.ToString());
        Assert.True(profile.GetProperty("isComplete").GetBoolean());
        Assert.Equal("Marta Gil Pérez", profile.GetProperty("legalName").GetString());
        Assert.Equal("12345678Z", profile.GetProperty("taxId").GetString());
        Assert.Equal("Calle Mayor 1", profile.GetProperty("addressLine").GetString());
        Assert.Equal("28001", profile.GetProperty("postalCode").GetString());
        Assert.Equal("Madrid", profile.GetProperty("city").GetString());
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("province").ValueKind);
        Assert.Equal("600123456", profile.GetProperty("phone").GetString());
        Assert.Equal("marta@example.com", profile.GetProperty("email").GetString());
        Assert.Equal(1, await RowsAsync(fixture.A.TenantId));
    }

    [Theory]
    [InlineData("12345678Z", "12345678Z")]
    [InlineData("12.345.678-z", "12345678Z")]
    [InlineData("x-1234567-l", "X1234567L")]
    [InlineData("Y1234567X", "Y1234567X")]
    [InlineData("a-5881850-1", "A58818501")]
    [InlineData("P2807900B", "P2807900B")]
    [InlineData("Q2826000H", "Q2826000H")]
    public async Task ANif_ANieAndACif_AreAcceptedInAnyWayOfTypingThem_AndStoredNormalized(string typed, string stored)
    {
        await PutOkAsync(A, Body(taxId: typed));

        Assert.Equal(stored, (await ReadAsync(A)).GetProperty("taxId").GetString());
    }

    [Fact]
    public async Task ASecondSave_ReplacesTheProfile_AndAnAbsentOptionalErasesThePreviousOne()
    {
        await PutOkAsync(A, Body());
        Assert.Equal("Madrid", (await ReadAsync(A)).GetProperty("province").GetString());

        // Sin provincia, teléfono ni email (ausentes del cuerpo, no solo nulos).
        var withoutOptionals = Body(legalName: "Fontanería Marta S.L.", taxId: "A58818501", postalCode: "08001", city: "Barcelona");
        withoutOptionals.Remove("province");
        withoutOptionals.Remove("phone");
        withoutOptionals.Remove("email");
        await PutOkAsync(A, withoutOptionals);

        var profile = await ReadAsync(A);
        Assert.Equal("Fontanería Marta S.L.", profile.GetProperty("legalName").GetString());
        Assert.Equal("A58818501", profile.GetProperty("taxId").GetString());
        Assert.Equal("Barcelona", profile.GetProperty("city").GetString());
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("province").ValueKind);
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("phone").ValueKind);
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("email").ValueKind);
        Assert.Equal(1, await RowsAsync(fixture.A.TenantId));
    }

    // ---------- La empresa sale solo del token ----------

    [Fact]
    public async Task ACompanyOrAnIdInTheBody_IsIgnored_TheProfileIsAlwaysThatOfTheToken()
    {
        await ResetAsync(fixture.A.TenantId);
        await ResetAsync(fixture.B.TenantId);
        var body = Body();
        body["tenantId"] = fixture.B.TenantId; // intenta escribir en la empresa de B
        body["id"] = Guid.NewGuid();
        body["isComplete"] = false;
        body["createdAt"] = "2001-01-01T00:00:00Z";

        await PutOkAsync(A, body);

        Assert.Equal(1, await RowsAsync(fixture.A.TenantId));
        Assert.Equal(0, await RowsAsync(fixture.B.TenantId));
        Assert.True((await ReadAsync(A)).GetProperty("isComplete").GetBoolean());
        Assert.False((await ReadAsync(B)).GetProperty("isComplete").GetBoolean()); // B sigue sin perfil
    }

    [Fact]
    public async Task EachCompany_SeesAndChangesOnlyItsOwnProfile_AndTheSameTaxIdInTwoCompaniesIsNotAnError()
    {
        await PutOkAsync(A, Body(legalName: "Empresa de A", taxId: "12345678Z"));
        await PutOkAsync(B, Body(legalName: "Empresa de B", taxId: "12345678Z")); // el mismo NIF: ni error ni respuesta distinta

        Assert.Equal("Empresa de A", (await ReadAsync(A)).GetProperty("legalName").GetString());
        Assert.Equal("Empresa de B", (await ReadAsync(B)).GetProperty("legalName").GetString());

        await PutOkAsync(B, Body(legalName: "B cambiada", taxId: "A58818501"));

        Assert.Equal("Empresa de A", (await ReadAsync(A)).GetProperty("legalName").GetString());
        Assert.Equal("12345678Z", (await ReadAsync(A)).GetProperty("taxId").GetString());
        Assert.Equal(1, await RowsAsync(fixture.A.TenantId));
        Assert.Equal(1, await RowsAsync(fixture.B.TenantId));
    }

    // ---------- Los errores de campo ----------

    [Theory]
    [InlineData("12345678A")] // la letra no cuadra
    [InlineData("X1234567A")]
    [InlineData("A58818502")]
    [InlineData("A5881850A")] // A exige dígito
    [InlineData("K1234567D")] // K no se admite
    [InlineData("12345678")]
    [InlineData("hola")]
    [InlineData("'; DROP TABLE--")]
    public async Task AWrongTaxId_IsAFieldError_WithItsOwnCode_AndChangesNothing(string taxId)
    {
        await PutOkAsync(A, Body(legalName: "Intacto"));

        var response = await A.PutAsJsonAsync(Route, Body(legalName: "No debe guardarse", taxId: taxId));

        await AssertFieldErrorAsync(response, "TaxId", "company.tax_id_invalid");
        var profile = await ReadAsync(A);
        Assert.Equal("Intacto", profile.GetProperty("legalName").GetString());
        Assert.Equal("12345678Z", profile.GetProperty("taxId").GetString());
    }

    [Fact]
    public async Task ATaxIdLongerThanTheInputLimit_IsAFieldError_ByLength_AndNeverReachesTheDatabase()
    {
        await PutOkAsync(A, Body(legalName: "Intacto"));

        var response = await A.PutAsJsonAsync(Route, Body(legalName: "No debe guardarse", taxId: "'; DROP TABLE CompanyProfiles; --"));

        await AssertFieldErrorAsync(response, "TaxId");
        Assert.Equal("Intacto", (await ReadAsync(A)).GetProperty("legalName").GetString());
    }

    [Theory]
    [InlineData("00001")]
    [InlineData("53001")]
    [InlineData("2800")]
    [InlineData("280011")]
    [InlineData("28 01")]
    [InlineData("2800A")]
    public async Task AWrongPostalCode_IsAFieldError_WithItsOwnCode_AndChangesNothing(string postalCode)
    {
        await PutOkAsync(A, Body(legalName: "Intacto"));

        var response = await A.PutAsJsonAsync(Route, Body(legalName: "No debe guardarse", postalCode: postalCode));

        await AssertFieldErrorAsync(response, "PostalCode", "company.postal_code_invalid");
        Assert.Equal("Intacto", (await ReadAsync(A)).GetProperty("legalName").GetString());
    }

    [Theory]
    [InlineData("legalName", "LegalName")]
    [InlineData("taxId", "TaxId")]
    [InlineData("addressLine", "AddressLine")]
    [InlineData("postalCode", "PostalCode")]
    [InlineData("city", "City")]
    public async Task EachRequiredField_IsRequired_WhetherAbsentNullEmptyOrBlank_AndNeverA500(string property, string field)
    {
        await PutOkAsync(A, Body(legalName: "Intacto"));

        foreach (var variant in new object?[] { "<absent>", null, "", "   " })
        {
            var body = Body(legalName: "No debe guardarse");
            if (variant is "<absent>")
            {
                body.Remove(property);
            }
            else
            {
                body[property] = variant;
            }

            await AssertFieldErrorAsync(await A.PutAsJsonAsync(Route, body), field);
        }

        Assert.Equal("Intacto", (await ReadAsync(A)).GetProperty("legalName").GetString());
    }

    [Fact]
    public async Task AnEmptyBody_GivesAFieldErrorPerRequiredField_AndAMalformedOneGives400_NeverA500()
    {
        var empty = await A.PutAsJsonAsync(Route, new { });
        var problem = await Dictionary.ReadProblemAsync(empty);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        var fields = problem.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("field").GetString()!.ToLowerInvariant()).ToHashSet();
        Assert.Superset(new HashSet<string> { "legalname", "taxid", "addressline", "postalcode", "city" }, fields);

        var malformed = await A.PutAsync(Route, new StringContent("{ esto no es json", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        var nullBody = await A.PutAsync(Route, new StringContent("null", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, nullBody.StatusCode);
    }

    [Theory]
    [InlineData("legalName", 200)]
    [InlineData("addressLine", 200)]
    [InlineData("city", 100)]
    [InlineData("province", 100)]
    public async Task EachTextLimit_AcceptsTheLimit_AndRejectsOneOver(string property, int limit)
    {
        var atLimit = Body();
        atLimit[property] = new string('a', limit);
        await PutOkAsync(A, atLimit);

        var over = Body(legalName: "No debe guardarse");
        over[property] = new string('a', limit + 1);
        await AssertFieldErrorAsync(await A.PutAsJsonAsync(Route, over), property);

        Assert.Equal(limit, (await ReadAsync(A)).GetProperty(property).GetString()!.Length);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12345")]
    [InlineData("+34 600 123 456 789 000 111")]
    public async Task AWrongPhone_IsAFieldError_WithTheCustomersPhoneCode(string phone)
    {
        await AssertFieldErrorAsync(await A.PutAsJsonAsync(Route, Body(phone: phone)), "Phone", phone.Length > 20 ? null : "customer.phone_invalid");
    }

    [Theory]
    [InlineData("sin-arroba")]
    [InlineData("a@b")]
    [InlineData("dos@@example.com")]
    public async Task AWrongEmail_IsAFieldError_WithTheCommonEmailCode(string email)
    {
        await AssertFieldErrorAsync(await A.PutAsJsonAsync(Route, Body(email: email)), "Email", "common.email_invalid");
    }

    [Fact]
    public async Task TheMessages_AreInTheDictionary_AndNeverRepeatTheValue()
    {
        var response = await A.PutAsJsonAsync(Route, Body(taxId: "12345678A", postalCode: "99999"));
        var problem = await Dictionary.ReadProblemAsync(response);
        var errors = problem.GetProperty("errors").EnumerateArray().ToList();

        var tax = errors.Single(e => e.GetProperty("code").GetString() == "company.tax_id_invalid");
        var postal = errors.Single(e => e.GetProperty("code").GetString() == "company.postal_code_invalid");
        Assert.Equal(Dictionary.Spanish("company.tax_id_invalid"), tax.GetProperty("message").GetString());
        Assert.Equal(Dictionary.Spanish("company.postal_code_invalid"), postal.GetProperty("message").GetString());
        Assert.DoesNotContain("12345678A", await response.Content.ReadAsStringAsync());
    }

    // ---------- Sin sesión ----------

    [Fact]
    public async Task WithoutASession_BothRoutesAre401()
    {
        var anonymous = fixture.Anonymous;

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Route)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(Route, Body())).StatusCode);
    }

    // ---------- La carrera de dos altas ----------

    [Fact]
    public async Task SeveralFirstSavesAtTheSameTime_LeaveOneProfile_AndNoneFails()
    {
        for (var round = 0; round < 4; round++)
        {
            await ResetAsync(fixture.B.TenantId);
            var names = Enumerable.Range(0, 4).Select(i => $"Intento {round}-{i}").ToArray();

            var responses = await Task.WhenAll(names.Select(name => Task.Run(() => B.PutAsJsonAsync(Route, Body(legalName: name)))));

            Assert.All(responses, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
            Assert.Equal(1, await RowsAsync(fixture.B.TenantId));
            Assert.Contains((await ReadAsync(B)).GetProperty("legalName").GetString(), names); // gana la última escritura: uno de ellos, entero
        }
    }

    // ---------- El registro: quién y cuándo, nunca qué ----------

    [Fact]
    public async Task EachSuccessfulSave_LeavesOneEvent1409WithTheCompanyAndNoDataOfTheProfile()
    {
        await ResetAsync(fixture.A.TenantId);
        var marker = $"secreto{Guid.NewGuid():N}";
        var before = fixture.Factory.Logs.Entries.Count;

        await PutOkAsync(A, Body(legalName: $"Nombre {marker}", addressLine: $"Calle {marker}", city: $"Ciudad {marker}", province: $"Provincia {marker}", phone: "611999888", email: $"{marker}@example.com", taxId: "x-1234567-l"));
        await PutOkAsync(A, Body(legalName: $"Otro {marker}", addressLine: $"Otra calle {marker}", phone: "611999777", email: $"{marker}.2@example.com", taxId: "A58818501"));
        await A.PutAsJsonAsync(Route, Body(legalName: $"Rechazado {marker}", taxId: "12345678A")); // un 400: también sin rastro
        await A.GetAsync(Route);

        var entries = fixture.Factory.Logs.Entries.Skip(before).ToList();
        var events = entries.Where(e => e.EventId == SecurityEventIds.CompanyProfileChanged).ToList();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Contains(fixture.A.TenantId.ToString(), e.Message, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("creado True", events[0].Message, StringComparison.Ordinal);
        Assert.Contains("creado False", events[1].Message, StringComparison.Ordinal);

        foreach (var secret in new[] { marker, "X1234567L", "A58818501", "611999888", "611999777", "12345678A" })
        {
            Assert.DoesNotContain(entries, e => e.Message.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }

        Assert.DoesNotContain(entries, e => e.EventId == SecurityEventIds.UnhandledException);
    }
}
