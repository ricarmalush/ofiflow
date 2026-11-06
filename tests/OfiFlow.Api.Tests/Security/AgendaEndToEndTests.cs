using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OfiFlow.Api.Tests.Errors;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// <c>GET /jobs/agenda</c> por HTTP (spec 020, bloque 2), con la API real, SQL Server real y dos empresas registradas de verdad. Cada test
/// usa su propia semana (lejos de las demás) para que lo que programa uno no aparezca en la agenda de otro: las dos empresas se comparten
/// dentro de la clase.
/// </summary>
public class AgendaEndToEndTests(TwoTenantsFixture fixture) : IClassFixture<TwoTenantsFixture>
{
    private HttpClient A => fixture.A.Client;

    private HttpClient B => fixture.B.Client;

    /// <summary>
    /// El lunes del hueco n-ésimo, a las 00:00 UTC. Cada test usa un hueco distinto y cada hueco son TRES semanas (el test usa la primera y,
    /// a lo sumo, la segunda): así un trabajo puesto en el borde de una semana no cae en la agenda de otro test.
    /// </summary>
    private static DateTime Monday(int slot) => new DateTime(2031, 1, 6, 0, 0, 0, DateTimeKind.Utc).AddDays(21 * slot);

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static string Week(int slot, int offsetWeeks = 0) =>
        $"from={Uri.EscapeDataString(Iso(Monday(slot).AddDays(7 * offsetWeeks)))}&to={Uri.EscapeDataString(Iso(Monday(slot).AddDays(7 * (offsetWeeks + 1))))}";

    private async Task<Guid> ScheduledJobAsync(HttpClient client, Guid customerId, string title, DateTime at, int? minutes = null)
    {
        var id = await TwoTenantsFixture.CreateJobAsync(client, customerId, title);
        var response = await client.PutAsJsonAsync($"/api/v1/jobs/{id}/schedule", new { scheduledDate = Iso(at), estimatedDurationMinutes = minutes });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        return id;
    }

    private async Task<Guid> CustomerOfBAsync() => await TwoTenantsFixture.CreateCustomerAsync(B, "Cliente de B");

    private static async Task<List<JsonElement>> ReadAgendaAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<JsonElement>>())!;
    }

    private static List<string?> Titles(List<JsonElement> agenda) => agenda.Select(j => j.GetProperty("title").GetString()).ToList();

    // ---- Lo que sale y en qué orden ----

    [Fact]
    public async Task TheWeek_ReturnsTheCompanysJobs_OrderedByDate_WithTheZ_AndNeverTheOtherCompanys()
    {
        var week = Monday(0);
        await ScheduledJobAsync(A, fixture.CustomerIdOfA, "Jueves", week.AddDays(3).AddHours(10));
        await ScheduledJobAsync(A, fixture.CustomerIdOfA, "Lunes tarde", week.AddHours(15), 60);
        await ScheduledJobAsync(A, fixture.CustomerIdOfA, "Lunes mañana", week.AddHours(9));
        await ScheduledJobAsync(B, await CustomerOfBAsync(), "De la otra empresa", week.AddHours(9));

        var agendaA = await ReadAgendaAsync(await A.GetAsync($"/api/v1/jobs/agenda?{Week(0)}"));
        var agendaB = await ReadAgendaAsync(await B.GetAsync($"/api/v1/jobs/agenda?{Week(0)}"));

        Assert.Equal(["Lunes mañana", "Lunes tarde", "Jueves"], Titles(agendaA));
        Assert.Equal(["De la otra empresa"], Titles(agendaB));
        Assert.Equal("2031-01-06T09:00:00Z", agendaA[0].GetProperty("scheduledDate").GetString());
        Assert.All(agendaA, job => Assert.EndsWith("Z", job.GetProperty("scheduledDate").GetString()));
        Assert.Equal(60, agendaA[1].GetProperty("estimatedDurationMinutes").GetInt32());
    }

    [Fact]
    public async Task TheRangeIsHalfOpen_TheStartIsIn_TheEndIsOut()
    {
        var week = Monday(1);
        await ScheduledJobAsync(A, fixture.CustomerIdOfA, "Al empezar", week);
        await ScheduledJobAsync(A, fixture.CustomerIdOfA, "Último segundo", week.AddDays(7).AddSeconds(-1));
        await ScheduledJobAsync(A, fixture.CustomerIdOfA, "Al terminar", week.AddDays(7));
        await ScheduledJobAsync(A, fixture.CustomerIdOfA, "Un segundo antes", week.AddSeconds(-1));

        var agenda = await ReadAgendaAsync(await A.GetAsync($"/api/v1/jobs/agenda?{Week(1)}"));
        var nextWeek = await ReadAgendaAsync(await A.GetAsync($"/api/v1/jobs/agenda?{Week(1, offsetWeeks: 1)}"));

        Assert.Equal(["Al empezar", "Último segundo"], Titles(agenda));
        Assert.Equal(["Al terminar"], Titles(nextWeek)); // el instante del final es de la semana siguiente
    }

    [Fact]
    public async Task ScheduledInProgressAndCompleted_AreShown_CancelledAndUnscheduledAreNot()
    {
        var week = Monday(3);
        await ScheduledJobAsync(A, fixture.CustomerIdOfA, "Programado", week.AddHours(1));
        var inProgress = await ScheduledJobAsync(A, fixture.CustomerIdOfA, "En curso", week.AddHours(2));
        await A.PostAsync($"/api/v1/jobs/{inProgress}/start", content: null);
        var completed = await ScheduledJobAsync(A, fixture.CustomerIdOfA, "Completado", week.AddHours(3));
        await A.PostAsync($"/api/v1/jobs/{completed}/start", content: null);
        await A.PostAsync($"/api/v1/jobs/{completed}/complete", content: null);
        var cancelled = await ScheduledJobAsync(A, fixture.CustomerIdOfA, "Cancelado", week.AddHours(4));
        await A.PostAsync($"/api/v1/jobs/{cancelled}/cancel", content: null);
        await TwoTenantsFixture.CreateJobAsync(A, fixture.CustomerIdOfA, "Sin fecha");

        var agenda = await ReadAgendaAsync(await A.GetAsync($"/api/v1/jobs/agenda?{Week(3)}"));

        Assert.Equal(["Programado", "En curso", "Completado"], Titles(agenda));
        Assert.Equal(["Scheduled", "InProgress", "Completed"], agenda.Select(j => j.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task ADayOfMadridInNovember_IsAskedWithItsOffset_AndTheJobsLandInTheRightDay()
    {
        // Lunes 4 de la semana: 09:00 en Madrid (+01:00) son las 08:00Z. El día de Madrid va de las 23:00Z del domingo a las 23:00Z del lunes.
        var monday = Monday(4);
        await ScheduledJobAsync(A, fixture.CustomerIdOfA, "Lunes 09:00 Madrid", monday.AddHours(8));
        await ScheduledJobAsync(A, fixture.CustomerIdOfA, "Domingo 23:30 Madrid", monday.AddMinutes(-90)); // 22:30Z del domingo = 23:30 Madrid

        var from = Uri.EscapeDataString(monday.ToString("yyyy-MM-dd'T'00:00:00", System.Globalization.CultureInfo.InvariantCulture) + "+01:00");
        var to = Uri.EscapeDataString(monday.AddDays(1).ToString("yyyy-MM-dd'T'00:00:00", System.Globalization.CultureInfo.InvariantCulture) + "+01:00");
        var agenda = await ReadAgendaAsync(await A.GetAsync($"/api/v1/jobs/agenda?from={from}&to={to}"));

        Assert.Equal(["Lunes 09:00 Madrid"], Titles(agenda));
    }

    [Fact]
    public async Task AnEmptyRange_Returns200WithAnEmptyList()
    {
        var agenda = await ReadAgendaAsync(await A.GetAsync($"/api/v1/jobs/agenda?{Week(5)}"));

        Assert.Empty(agenda);
    }

    // ---- Rangos que no valen: un 400 de campo ----

    [Theory]
    [InlineData("from=2031-02-03T00:00:00Z", "From o To ausente")]
    [InlineData("to=2031-02-10T00:00:00Z", "From o To ausente")]
    [InlineData("", "ninguno")]
    public async Task AMissingEndpoint_Gets400_NotA500(string query, string _)
    {
        var response = await A.GetAsync($"/api/v1/jobs/agenda?{query}");
        var problem = await Dictionary.ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        Assert.Contains(problem.GetProperty("errors").EnumerateArray(), e => e.GetProperty("code").GetString() == JobErrors.AgendaRangeInvalid);
    }

    [Theory]
    [InlineData("from=2031-02-03T00:00:00&to=2031-02-10T00:00:00Z", "From")]      // sin desfase
    [InlineData("from=2031-02-03T00:00:00Z&to=2031-02-10T00:00:00", "To")]
    [InlineData("from=2031-02-03&to=2031-02-10", "From")]                         // solo fecha
    [InlineData("from=lunes&to=domingo", "From")]
    [InlineData("from=2031-02-10T00:00:00Z&to=2031-02-03T00:00:00Z", "To")]       // el final antes del principio
    [InlineData("from=2031-02-03T00:00:00Z&to=2031-02-03T00:00:00Z", "To")]       // vacío
    public async Task AnInvalidRange_Gets400WithTheFieldAndItsCode(string query, string field)
    {
        var response = await A.GetAsync($"/api/v1/jobs/agenda?{query}");
        var problem = await Dictionary.ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        var error = problem.GetProperty("errors").EnumerateArray().First(e => e.GetProperty("field").GetString() == field);
        Assert.Equal(JobErrors.AgendaRangeInvalid, error.GetProperty("code").GetString());
        Assert.Equal(Dictionary.Spanish(JobErrors.AgendaRangeInvalid), error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task AnUnencodedPlusInTheOffset_Gets400_TheClientMustEncodeIt()
    {
        // En una URL, «+» sin codificar llega como un espacio: «+01:00» pasa a « 01:00», que no es un desfase. Se rechaza en vez de adivinar.
        var response = await A.GetAsync("/api/v1/jobs/agenda?from=2031-02-03T00:00:00+01:00&to=2031-02-10T00:00:00+01:00");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheMaximumRange_Of42Days_IsAccepted_AndOneSecondMoreIsNot()
    {
        var from = Monday(6);
        string Range(DateTime end) => $"from={Uri.EscapeDataString(Iso(from))}&to={Uri.EscapeDataString(Iso(end))}";

        var exact = await A.GetAsync($"/api/v1/jobs/agenda?{Range(from.AddDays(42))}");
        var over = await A.GetAsync($"/api/v1/jobs/agenda?{Range(from.AddDays(42).AddSeconds(1))}");

        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
    }

    // ---- La ruta, y sin sesión ----

    [Fact]
    public async Task TheAgendaRoute_DoesNotCollideWithTheJobById_BothStillWork()
    {
        // «agenda» no es un Guid: llega al endpoint de la agenda (y sin parámetros da el 400 del validador, no un 404 ni un 405)...
        var agenda = await A.GetAsync("/api/v1/jobs/agenda");
        // ...y un Guid sigue siendo un trabajo.
        var byId = await A.GetAsync($"/api/v1/jobs/{fixture.JobIdOfA}");

        Assert.Equal(HttpStatusCode.BadRequest, agenda.StatusCode);
        Assert.Equal("validation.failed", (await Dictionary.ReadProblemAsync(agenda)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);
    }

    [Fact]
    public async Task WithoutAToken_TheAgendaGets401()
    {
        var response = await fixture.Anonymous.GetAsync($"/api/v1/jobs/agenda?{Week(0)}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
