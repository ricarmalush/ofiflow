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
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// El permiso de mandar un presupuesto por correo (spec 026 R16, R7, D7) por HTTP, con la API real, SQL Server real y un usuario de cada rol que ha hecho login de verdad. Se pide <c>Quotes.Issue</c>,
/// el de «Enviar»: Owner, Admin y Manager mandan; Employee (que prepara y descarga el PDF) y Technician, no. La autorización va <b>antes</b> que todo lo demás (el mismo 403 exista o no el presupuesto, tenga
/// o no la empresa su perfil), y quien no tiene el permiso <b>no gasta cupo</b> del límite por persona.
/// </summary>
public class QuoteEmailRolesEndToEndTests(RolesFixture fixture) : IClassFixture<RolesFixture>, IAsyncLifetime
{
    private static readonly TenantRole[] Columns =
        [TenantRole.Owner, TenantRole.Admin, TenantRole.Manager, TenantRole.Employee, TenantRole.Technician];

    // Escrita aparte de RolePermissions y de la tabla de operaciones: copias independientes que tienen que coincidir. X = permitido, - = denegado.
    private static readonly string[] Matrix =
    [
        //             Owner Admin Manager Employee Technician
        "quote_email   X     X     X       -        -"
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

    private readonly RecordingEmailSender _email = new();
    private WebApplicationFactory<Program> _factory = null!;
    private readonly Dictionary<TenantRole, HttpClient> _clients = [];

    private HttpClient As(TenantRole role) => _clients[role];

    // Cada test empieza con la empresa con su perfil fiscal guardado y un host propio (con su propio límite por persona).
    public async Task InitializeAsync()
    {
        _factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(_email);
        }));

        foreach (var role in Columns)
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = fixture.ClientOf(role).DefaultRequestHeaders.Authorization;
            _clients[role] = client;
        }

        var response = await fixture.ClientOf(TenantRole.Owner).PutAsJsonAsync("/api/v1/company/profile", new
        {
            legalName = "Fontanería Marta Gil S.L.",
            taxId = "B84531946",
            addressLine = "Calle de Alcalá 123",
            postalCode = "28009",
            city = "Madrid"
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public Task DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task RemoveProfileAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CompanyProfiles.IgnoreQueryFilters().Where(p => p.TenantId == fixture.TenantId).ExecuteDeleteAsync();
    }

    private async Task<Guid> NewQuoteAsync()
    {
        var owner = fixture.ClientOf(TenantRole.Owner);
        var customer = await TwoTenantsFixture.CreateCustomerAsync(owner, $"Cliente {Guid.NewGuid():N}");
        var response = await owner.PostAsJsonAsync("/api/v1/quotes", new { customerId = customer, title = "Reforma", items = new[] { new { description = "Mano de obra", quantity = 1, unitPriceCents = 10000L, vatPercent = 21 } } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> EmailAsync(HttpClient client, Guid id, string recipient = "cliente@ejemplo.es")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/quotes/{id}/email") { Content = JsonContent.Create(new { recipient }) };
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, TestClientIpStartupFilter.NewIp());
        return client.SendAsync(request);
    }

    private static async Task AssertForbiddenAsync(HttpResponseMessage response)
    {
        var problem = await Dictionary.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.forbidden", problem.GetProperty("code").GetString());
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task MailingAQuote_IsAllowedToTheRolesThatCanIssueQuotes_AndDeniedToTheOthers(TenantRole role, bool allowed)
    {
        var id = await NewQuoteAsync();

        var response = await EmailAsync(As(role), id);

        if (allowed)
        {
            // Control positivo: no solo un 200, sino un correo de verdad con su PDF adjunto.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var message = Assert.Single(_email.Sent);
            Assert.StartsWith("Presupuesto P-", message.Subject, StringComparison.Ordinal);
            Assert.Contains("Fontanería Marta Gil S.L.", PdfReading.Of(message.Attachment!.Content).Lines);
        }
        else
        {
            await AssertForbiddenAsync(response);
            Assert.Empty(_email.Sent);
        }
    }

    [Fact]
    public async Task AnEmployee_CanDownloadThePdf_ButCannotMailIt()
    {
        // El Employee prepara (lee y escribe) pero no compromete: puede descargar el PDF y no mandarlo a un tercero.
        var id = await NewQuoteAsync();
        var employee = As(TenantRole.Employee);

        var download = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/quotes/{id}/pdf");
        download.Headers.Add(TestClientIpStartupFilter.HeaderName, TestClientIpStartupFilter.NewIp());

        Assert.Equal(HttpStatusCode.OK, (await employee.SendAsync(download)).StatusCode);
        await AssertForbiddenAsync(await EmailAsync(employee, id));
    }

    [Fact]
    public async Task ATechnicianAndAnEmployee_GetTheSame403ForAQuoteThatExistsAndForOneThatDoesNot()
    {
        var existing = await NewQuoteAsync();

        foreach (var role in new[] { TenantRole.Employee, TenantRole.Technician })
        {
            await AssertForbiddenAsync(await EmailAsync(As(role), existing));
            await AssertForbiddenAsync(await EmailAsync(As(role), Guid.NewGuid()));
        }

        // Control positivo: quien sí tiene el permiso distingue los dos casos (200 y 404).
        Assert.Equal(HttpStatusCode.OK, (await EmailAsync(As(TenantRole.Manager), existing)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await EmailAsync(As(TenantRole.Manager), Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task TheAuthorizationComesBeforeTheFiscalProfileAndTheAddress()
    {
        var id = await NewQuoteAsync();
        await RemoveProfileAsync();

        // Sin permiso: el mismo 403, sin enterarse de si la empresa tiene perfil ni de si la dirección valía.
        await AssertForbiddenAsync(await EmailAsync(As(TenantRole.Employee), id));
        await AssertForbiddenAsync(await EmailAsync(As(TenantRole.Technician), id, "no-es-una-direccion"));

        // Con permiso: el error de negocio.
        var problem = await Dictionary.ReadProblemAsync(await EmailAsync(As(TenantRole.Manager), id));
        Assert.Equal("quote.company_profile_incomplete", problem.GetProperty("code").GetString());
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task WhoCannotMailDoesNotSpendThePerPersonAllowance()
    {
        // 25 intentos de un Employee: todos 403. Si gastaran cupo (20 por hora), los últimos serían 429; y el cupo de otra persona de su mismo rol no se toca.
        var id = await NewQuoteAsync();
        var employee = As(TenantRole.Employee);

        for (var i = 0; i < QuoteEmailThrottle.PermitLimit + 5; i++)
        {
            await AssertForbiddenAsync(await EmailAsync(employee, id));
        }

        // Y quien sí puede sigue pudiendo: su cupo es suyo.
        Assert.Equal(HttpStatusCode.OK, (await EmailAsync(As(TenantRole.Owner), id)).StatusCode);
    }

    [Fact]
    public async Task EachPerson_HasTheirOwnAllowance()
    {
        var id = await NewQuoteAsync();

        for (var i = 0; i < QuoteEmailThrottle.PermitLimit; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await EmailAsync(As(TenantRole.Owner), id)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await EmailAsync(As(TenantRole.Owner), id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EmailAsync(As(TenantRole.Admin), id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EmailAsync(As(TenantRole.Manager), id)).StatusCode);
    }

    [Fact]
    public async Task WithoutASession_ThereIsA401_BeforeAnyOtherCheck()
    {
        var id = await NewQuoteAsync();
        using var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await EmailAsync(anonymous, id)).StatusCode);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task EveryRoleThatCanMailAQuote_CanAlsoReadTheQuoteAndTheFiscalProfileItShows()
    {
        // La invariante de la D7 comprobada por HTTP: quien manda el PDF con los datos de la empresa puede leer esos datos y el presupuesto por su propia ruta.
        var id = await NewQuoteAsync();

        foreach (var role in new[] { TenantRole.Owner, TenantRole.Admin, TenantRole.Manager })
        {
            Assert.Equal(HttpStatusCode.OK, (await As(role).GetAsync($"/api/v1/quotes/{id}")).StatusCode);
            var profile = await As(role).GetAsync("/api/v1/company/profile");
            Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
            Assert.Equal("B84531946", (await profile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("taxId").GetString());
        }
    }
}
