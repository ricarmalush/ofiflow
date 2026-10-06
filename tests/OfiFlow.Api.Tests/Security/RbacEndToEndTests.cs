using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using OfiFlow.Application.Customers;
using OfiFlow.Application.Jobs;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// Spec 008 por HTTP, con la API real, SQL Server real y un usuario de cada rol que ha hecho login de verdad:
/// quién puede y quién no puede hacer cada operación. Cada denegación se acompaña de la comprobación de que
/// el dato no cambió, y cada permiso concedido, de que la operación tuvo efecto.
/// </summary>
public class RbacEndToEndTests(RolesFixture fixture) : IClassFixture<RolesFixture>
{
    // La matriz aprobada en la spec 008, por operación y rol, escrita aparte de RolePermissions y de la tabla del
    // test de Domain: tres copias independientes que tienen que coincidir. X = permitido, - = denegado.
    private static readonly TenantRole[] Columns =
        [TenantRole.Owner, TenantRole.Admin, TenantRole.Manager, TenantRole.Employee, TenantRole.Technician];

    private static readonly string[] Matrix =
    [
        //                    Owner Admin Manager Employee Technician
        "customers_list       X     X     X       X        X",
        "customer_get         X     X     X       X        X",
        "customer_create      X     X     X       X        -",
        "customer_update      X     X     X       X        -",
        "customer_delete      X     X     -       -        -",
        "jobs_list            X     X     X       X        X",
        "job_get              X     X     X       X        X",
        "job_create           X     X     X       X        -",
        "job_update           X     X     X       X        -",
        "job_assign           X     X     X       -        -",
        "job_cancel           X     X     X       -        -",
        "job_start            X     X     X       -        X",
        "job_complete         X     X     X       -        X"
    ];

    public static TheoryData<string, TenantRole, bool> Cells()
    {
        var data = new TheoryData<string, TenantRole, bool>();

        foreach (var row in Matrix)
        {
            var parts = row.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            for (var column = 0; column < Columns.Length; column++)
            {
                data.Add(parts[0], Columns[column], parts[column + 1] == "X");
            }
        }

        return data;
    }

    // --- La matriz completa: 13 operaciones × 5 roles ---

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task EachRole_CanOrCannotPerformEachOperation(string operation, TenantRole role, bool allowed)
    {
        var context = await ArrangeAsync(operation);
        var definition = Operations[operation];

        var response = await fixture.ClientOf(role).SendAsync(definition.Request(fixture, context));

        if (allowed)
        {
            Assert.Equal(definition.Success, response.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("auth.forbidden", (await ReadProblemAsync(response)).Code);
        }

        await definition.VerifyAsync(fixture, context, allowed);
    }

    [Fact]
    public void TheMatrixCoversEveryOperationAndEveryRole()
    {
        // Sin esto, añadir una operación a la tabla de operaciones y olvidarla en la matriz (o al revés) dejaría casos sin ejecutar.
        var rows = Matrix.Select(row => row.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]).Order().ToList();

        Assert.Equal(Operations.Keys.Order(), rows);
        Assert.Equal(Enum.GetValues<TenantRole>().Order(), Columns.Order());
    }

    // --- La autorización va antes que la validación ---

    [Fact]
    public async Task WithoutPermission_InvalidDataGets403_NotA400()
    {
        // Técnico: sin Jobs.Write. Un título vacío lo rechazaría el validador, pero la autorización responde primero.
        var forbidden = await fixture.ClientOf(TenantRole.Technician)
            .PostAsJsonAsync("/api/v1/jobs", new { customerId = Guid.NewGuid(), title = "", priority = 1 });

        // Control: Employee sí tiene Jobs.Write, así que con los mismos datos llega a la validación.
        var validated = await fixture.ClientOf(TenantRole.Employee)
            .PostAsJsonAsync("/api/v1/jobs", new { customerId = Guid.NewGuid(), title = "", priority = 1 });

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, validated.StatusCode);
        Assert.Equal("validation.failed", (await ReadProblemAsync(validated)).Code);
    }

    // --- Una denegación no depende del recurso pedido ---

    [Fact]
    public async Task ADenial_IsTheSameForAnotherCompanysIdAndForAnIdThatDoesNotExist()
    {
        // Técnico: sin Customers.Delete. Pide borrar un cliente de OTRA empresa y uno inexistente.
        var technician = fixture.ClientOf(TenantRole.Technician);
        var foreign = await technician.DeleteAsync($"/api/v1/customers/{fixture.CustomerIdOfAnotherTenant}");
        var nonexistent = await technician.DeleteAsync($"/api/v1/customers/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, nonexistent.StatusCode);
        Assert.Equal(await ReadProblemAsync(nonexistent), await ReadProblemAsync(foreign));

        // Control: quien SÍ tiene el permiso (Owner) recibe un 404 para ese mismo cliente ajeno: el aislamiento
        // por empresa sigue actuando después de la autorización.
        var owner = await fixture.ClientOf(TenantRole.Owner).DeleteAsync($"/api/v1/customers/{fixture.CustomerIdOfAnotherTenant}");
        Assert.Equal(HttpStatusCode.NotFound, owner.StatusCode);
    }

    // --- El rol sale del token: lo que el login nunca emite se deniega ---

    [Theory]
    [InlineData(null)]
    [InlineData("Superadmin")]
    [InlineData("owner")]
    [InlineData("1")]
    [InlineData("")]
    public async Task ATokenWithoutAValidRole_IsForbidden(string? role)
    {
        var owner = fixture.UserOf(TenantRole.Owner);
        using var client = fixture.ClientWithToken(fixture.MintToken(owner.UserId, fixture.TenantId, role));

        var response = await client.GetAsync("/api/v1/customers");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.forbidden", (await ReadProblemAsync(response)).Code);
    }

    [Fact]
    public async Task AMintedTokenWithAValidRole_IsAccepted()
    {
        // Control de los tests anteriores: fabricar el token no es lo que lo hace fallar.
        var owner = fixture.UserOf(TenantRole.Owner);
        using var client = fixture.ClientWithToken(fixture.MintToken(owner.UserId, fixture.TenantId, "Owner"));

        var response = await client.GetAsync("/api/v1/customers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- Las operaciones sin sesión siguen funcionando ---

    [Fact]
    public async Task TheAnonymousOperations_StillWorkWithoutAToken()
    {
        var email = $"rbac-nueva-{Guid.NewGuid():N}@example.com";
        // Generada en cada ejecución, como en el resto de tests: ninguna contraseña escrita en el código.
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        var register = await fixture.Anonymous.PostAsJsonAsync("/api/v1/auth/register",
            new { companyName = "Otra empresa", userName = "Otra persona", email, password });

        var refresh = await fixture.Anonymous.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = fixture.UserOf(TenantRole.Owner).RefreshToken });

        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
    }

    // --- Cada denegación queda en el registro de seguridad ---

    [Fact]
    public async Task ADenial_IsLoggedAsASecurityEvent_WithIdsAndRoleButNoRequestData()
    {
        var technician = fixture.UserOf(TenantRole.Technician);
        var secretName = $"Nombre-que-no-debe-salir-en-el-log-{Guid.NewGuid():N}";

        // Otros tests de la clase ya han generado denegaciones: se cuenta cuántas hay antes y después de esta petición.
        var deniedBefore = fixture.Factory.Logs.Entries.Count(e => e.EventId == 1301);

        var response = await technician.Client.PostAsJsonAsync("/api/v1/customers", new { type = 0, name = secretName });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var denials = fixture.Factory.Logs.Entries.Where(e => e.EventId == 1301).ToList();
        Assert.Equal(deniedBefore + 1, denials.Count);

        var entry = denials[^1];
        Assert.Contains(technician.UserId.ToString(), entry.Message);
        Assert.Contains("CreateCustomerCommand", entry.Message);
        Assert.Contains("Technician", entry.Message);
        Assert.Contains(fixture.TenantId.ToString(), entry.Message);
        Assert.DoesNotContain(fixture.Factory.Logs.Entries, e => e.Message.Contains(secretName));
    }

    // --- Cada operación: su petición, lo que responde si se permite, y la comprobación de su efecto ---

    private sealed record Context(Guid CustomerId, Guid FreeCustomerId, Guid JobId, string Unique)
    {
        public string BaseCustomerName => $"Cliente base {Unique}";

        public string BaseJobTitle => $"Trabajo base {Unique}";
    }

    private sealed record Operation(
        Func<RolesFixture, Context, HttpRequestMessage> Request,
        HttpStatusCode Success,
        Func<RolesFixture, Context, bool, Task> VerifyAsync);

    private static readonly Func<RolesFixture, Context, bool, Task> NothingToVerify = (_, _, _) => Task.CompletedTask;

    private static readonly Dictionary<string, Operation> Operations = new()
    {
        ["customers_list"] = new(
            (_, _) => new(HttpMethod.Get, "/api/v1/customers"), HttpStatusCode.OK, NothingToVerify),

        ["customer_get"] = new(
            (_, c) => new(HttpMethod.Get, $"/api/v1/customers/{c.CustomerId}"), HttpStatusCode.OK, NothingToVerify),

        ["customer_create"] = new(
            (_, c) => WithJson(HttpMethod.Post, "/api/v1/customers", new { type = 0, name = $"Alta {c.Unique}" }),
            HttpStatusCode.Created,
            async (f, c, allowed) =>
                Assert.Equal(allowed, (await ListCustomersAsync(f)).Any(customer => customer.Name == $"Alta {c.Unique}"))),

        ["customer_update"] = new(
            (_, c) => WithJson(HttpMethod.Put, $"/api/v1/customers/{c.CustomerId}", new { name = $"Renombrado {c.Unique}" }),
            HttpStatusCode.NoContent,
            async (f, c, allowed) =>
                Assert.Equal(allowed ? $"Renombrado {c.Unique}" : c.BaseCustomerName, (await GetCustomerAsync(f, c.CustomerId)).Name)),

        ["customer_delete"] = new(
            (_, c) => new(HttpMethod.Delete, $"/api/v1/customers/{c.FreeCustomerId}"),
            HttpStatusCode.NoContent,
            async (f, c, allowed) =>
                Assert.Equal(allowed ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                    (await f.ClientOf(TenantRole.Owner).GetAsync($"/api/v1/customers/{c.FreeCustomerId}")).StatusCode)),

        ["jobs_list"] = new(
            (_, _) => new(HttpMethod.Get, "/api/v1/jobs"), HttpStatusCode.OK, NothingToVerify),

        ["job_get"] = new(
            (_, c) => new(HttpMethod.Get, $"/api/v1/jobs/{c.JobId}"), HttpStatusCode.OK, NothingToVerify),

        ["job_create"] = new(
            (_, c) => WithJson(HttpMethod.Post, "/api/v1/jobs", new { customerId = c.CustomerId, title = $"Alta {c.Unique}", priority = 1 }),
            HttpStatusCode.Created,
            async (f, c, allowed) =>
                Assert.Equal(allowed, (await ListJobsAsync(f)).Any(job => job.Title == $"Alta {c.Unique}"))),

        ["job_update"] = new(
            (_, c) => WithJson(HttpMethod.Put, $"/api/v1/jobs/{c.JobId}", new { title = $"Cambiado {c.Unique}", priority = 1 }),
            HttpStatusCode.NoContent,
            async (f, c, allowed) =>
                Assert.Equal(allowed ? $"Cambiado {c.Unique}" : c.BaseJobTitle, (await GetJobAsync(f, c.JobId)).Title)),

        ["job_assign"] = new(
            (f, c) => WithJson(HttpMethod.Post, $"/api/v1/jobs/{c.JobId}/assign", new { tenantUserId = f.UserOf(TenantRole.Technician).TenantUserId }),
            HttpStatusCode.NoContent,
            async (f, c, allowed) =>
                Assert.Equal(allowed ? f.UserOf(TenantRole.Technician).TenantUserId : (Guid?)null, (await GetJobAsync(f, c.JobId)).AssignedTenantUserId)),

        ["job_cancel"] = new(
            (_, c) => new(HttpMethod.Post, $"/api/v1/jobs/{c.JobId}/cancel"),
            HttpStatusCode.NoContent,
            async (f, c, allowed) =>
                Assert.Equal(allowed ? "Cancelled" : "New", (await GetJobAsync(f, c.JobId)).Status)),

        ["job_start"] = new(
            (_, c) => new(HttpMethod.Post, $"/api/v1/jobs/{c.JobId}/start"),
            HttpStatusCode.NoContent,
            async (f, c, allowed) =>
                Assert.Equal(allowed ? "InProgress" : "New", (await GetJobAsync(f, c.JobId)).Status)),

        // El trabajo se prepara ya en curso (lo inicia el Owner): si se pudiera completar sin permiso, cambiaría.
        ["job_complete"] = new(
            (_, c) => new(HttpMethod.Post, $"/api/v1/jobs/{c.JobId}/complete"),
            HttpStatusCode.NoContent,
            async (f, c, allowed) =>
                Assert.Equal(allowed ? "Completed" : "InProgress", (await GetJobAsync(f, c.JobId)).Status))
    };

    /// <summary>Datos nuevos para cada caso, creados por el Owner, para que unos casos no se pisen con otros.</summary>
    private async Task<Context> ArrangeAsync(string operation)
    {
        var unique = Guid.NewGuid().ToString("N");
        var owner = fixture.ClientOf(TenantRole.Owner);

        var customerId = await CreateAsync(owner, "/api/v1/customers", new { type = 0, name = $"Cliente base {unique}" });
        var freeCustomerId = await CreateAsync(owner, "/api/v1/customers", new { type = 0, name = $"Cliente sin trabajos {unique}" });
        var jobId = await CreateAsync(owner, "/api/v1/jobs", new { customerId, title = $"Trabajo base {unique}", priority = 1 });

        if (operation == "job_complete")
        {
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/jobs/{jobId}/start", content: null)).StatusCode);
        }

        return new Context(customerId, freeCustomerId, jobId, unique);
    }

    private static HttpRequestMessage WithJson(HttpMethod method, string url, object body) =>
        new(method, url) { Content = JsonContent.Create(body) };

    private static async Task<Guid> CreateAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<Created>())!.Id;
    }

    private static async Task<List<CustomerDto>> ListCustomersAsync(RolesFixture f) =>
        (await f.ClientOf(TenantRole.Owner).GetFromJsonAsync<List<CustomerDto>>("/api/v1/customers"))!;

    private static async Task<List<JobDto>> ListJobsAsync(RolesFixture f) =>
        (await f.ClientOf(TenantRole.Owner).GetFromJsonAsync<List<JobDto>>("/api/v1/jobs"))!;

    private static async Task<CustomerDto> GetCustomerAsync(RolesFixture f, Guid id) =>
        (await f.ClientOf(TenantRole.Owner).GetFromJsonAsync<CustomerDto>($"/api/v1/customers/{id}"))!;

    private static async Task<JobDto> GetJobAsync(RolesFixture f, Guid id) =>
        (await f.ClientOf(TenantRole.Owner).GetFromJsonAsync<JobDto>($"/api/v1/jobs/{id}"))!;

    private static async Task<Problem> ReadProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Problem>())!;

    /// <summary>Los campos de un ProblemDetails que deben coincidir. El traceId se deja fuera a propósito: cambia en cada petición.</summary>
    private sealed record Problem(int? Status, string? Title, string? Detail, string? Code);

    private sealed record Created(Guid Id);
}
