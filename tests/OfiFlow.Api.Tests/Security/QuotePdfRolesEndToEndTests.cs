using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Api.Tests.Errors;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// El permiso del PDF de un presupuesto (spec 025 R11 y R12, D6) por HTTP, con la API real, SQL Server real y un usuario de cada rol que ha hecho login de verdad. La descarga pide <c>Quotes.Read</c>:
/// los cuatro roles que lo tienen la reciben (con control positivo de que es un PDF de verdad) y el <c>Technician</c> no. Y la autorización va <b>antes</b> que todo lo demás: sin permiso, el mismo
/// <c>403</c> exista o no el presupuesto y tenga o no la empresa su perfil fiscal.
/// </summary>
public class QuotePdfRolesEndToEndTests(RolesFixture fixture) : IClassFixture<RolesFixture>, IAsyncLifetime
{
    private static readonly TenantRole[] Columns =
        [TenantRole.Owner, TenantRole.Admin, TenantRole.Manager, TenantRole.Employee, TenantRole.Technician];

    // Escrita aparte de RolePermissions y de la tabla de operaciones: copias independientes que tienen que coincidir. X = permitido, - = denegado.
    private static readonly string[] Matrix =
    [
        //            Owner Admin Manager Employee Technician
        "quote_pdf    X     X     X       X        -"
    ];

    public static TheoryData<TenantRole, bool> Cells()
    {
        var data = new TheoryData<TenantRole, bool>();
        var parts = Matrix[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (var column = 0; column < Columns.Length; column++)
        {
            data.Add(Columns[column], parts[column + 1] == "X");
        }

        return data;
    }

    private HttpClient Owner => fixture.ClientOf(TenantRole.Owner);

    // Cada test empieza con la empresa con su perfil fiscal guardado; el que necesita lo contrario lo quita él mismo.
    public async Task InitializeAsync() => await SaveProfileAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SaveProfileAsync()
    {
        var response = await Owner.PutAsJsonAsync("/api/v1/company/profile", new
        {
            legalName = "Fontanería Marta Gil S.L.",
            taxId = "B84531946",
            addressLine = "Calle de Alcalá 123",
            postalCode = "28009",
            city = "Madrid"
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task RemoveProfileAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CompanyProfiles.IgnoreQueryFilters().Where(p => p.TenantId == fixture.TenantId).ExecuteDeleteAsync();
    }

    private async Task<Guid> NewQuoteAsync()
    {
        var customer = await TwoTenantsFixture.CreateCustomerAsync(Owner, $"Cliente {Guid.NewGuid():N}");
        var response = await Owner.PostAsJsonAsync("/api/v1/quotes", new { customerId = customer, title = "Reforma", items = new[] { new { description = "Mano de obra", quantity = 1, unitPriceCents = 10000L, vatPercent = 21 } } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> PdfAsync(HttpClient client, Guid id)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/quotes/{id}/pdf");
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, TestClientIpStartupFilter.NewIp());
        return client.SendAsync(request);
    }

    private static async Task AssertForbiddenAsync(HttpResponseMessage response)
    {
        var problem = await Dictionary.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.forbidden", problem.GetProperty("code").GetString());
        Assert.NotEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task ThePdf_IsAllowedToTheRolesThatCanReadQuotes_AndDeniedToTheOthers(TenantRole role, bool allowed)
    {
        var id = await NewQuoteAsync();

        var response = await PdfAsync(fixture.ClientOf(role), id);

        if (allowed)
        {
            // Control positivo: no solo un 200, sino un PDF de verdad con los datos de la empresa.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
            var pdf = await PdfReading.OfAsync(response);
            Assert.Contains("Fontanería Marta Gil S.L.", pdf.Lines);
            Assert.Contains("NIF B84531946", pdf.Lines);
        }
        else
        {
            await AssertForbiddenAsync(response);
        }
    }

    [Fact]
    public async Task ATechnician_GetsTheSame403ForAQuoteThatExistsAndForOneThatDoesNot()
    {
        var existing = await NewQuoteAsync();
        var technician = fixture.ClientOf(TenantRole.Technician);

        var forExisting = await PdfAsync(technician, existing);
        var forMissing = await PdfAsync(technician, Guid.NewGuid());

        await AssertForbiddenAsync(forExisting);
        await AssertForbiddenAsync(forMissing);
        // Control positivo: quien sí tiene el permiso distingue los dos casos (200 y 404).
        Assert.Equal(HttpStatusCode.OK, (await PdfAsync(Owner, existing)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PdfAsync(Owner, Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task TheAuthorizationComesBeforeTheFiscalProfile_ATechnicianGets403AndAnEmployeeGets400()
    {
        var id = await NewQuoteAsync();
        await RemoveProfileAsync();

        var technician = await PdfAsync(fixture.ClientOf(TenantRole.Technician), id);
        var employee = await PdfAsync(fixture.ClientOf(TenantRole.Employee), id);

        // Sin permiso: el mismo 403, sin enterarse de si la empresa tiene perfil.
        await AssertForbiddenAsync(technician);
        // Con permiso: el error de negocio.
        var problem = await Dictionary.ReadProblemAsync(employee);
        Assert.Equal(HttpStatusCode.BadRequest, employee.StatusCode);
        Assert.Equal("quote.company_profile_incomplete", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task WithoutASession_ThereIsA401_BeforeAnyOtherCheck()
    {
        var id = await NewQuoteAsync();
        var anonymous = fixture.Anonymous;

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/quotes/{id}/pdf");
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, TestClientIpStartupFilter.NewIp());

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task EveryRoleThatCanDownloadThePdfCanAlsoReadTheFiscalProfileItShows()
    {
        // La invariante de la D6 comprobada por HTTP: quien recibe el PDF con los datos de la empresa puede leer esos datos por su propia ruta.
        foreach (var role in new[] { TenantRole.Owner, TenantRole.Admin, TenantRole.Manager, TenantRole.Employee })
        {
            var profile = await fixture.ClientOf(role).GetAsync("/api/v1/company/profile");
            Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
            Assert.Equal("B84531946", (await profile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("taxId").GetString());
        }

        // Y el rol sin permiso sobre el PDF tampoco puede leer el perfil: no hay un camino alternativo a los mismos datos.
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.ClientOf(TenantRole.Technician).GetAsync("/api/v1/company/profile")).StatusCode);
    }
}
