using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OfiFlow.Api.Tests.Errors;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// Programar, reprogramar y desprogramar un trabajo por HTTP (spec 020, bloque 1), con la API real, SQL Server real y dos empresas
/// registradas de verdad. Lo que se comprueba es el contrato: la fecha entra con su desfase y <b>sale en UTC con la «Z»</b>, las
/// entradas inválidas dan un 400 de campo, cada estado que no admite programar da su código, y una empresa no toca la agenda de otra.
/// </summary>
public class JobSchedulingEndToEndTests(TwoTenantsFixture fixture) : IClassFixture<TwoTenantsFixture>
{
    private HttpClient A => fixture.A.Client;

    private HttpClient B => fixture.B.Client;

    private async Task<Guid> NewJobAsync() =>
        await TwoTenantsFixture.CreateJobAsync(A, fixture.CustomerIdOfA, $"Trabajo {Guid.NewGuid():N}");

    private static StringContent Json(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> ScheduleAsync(HttpClient client, Guid id, string? date, int? minutes = null) =>
        client.PutAsJsonAsync($"/api/v1/jobs/{id}/schedule", new { scheduledDate = date, estimatedDurationMinutes = minutes });

    private static async Task<JsonElement> GetJobJsonAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{id}")).Clone();

    // ---- El flujo completo ----

    [Fact]
    public async Task ScheduleThenRead_TheDateComesBackInUtcWithTheZ_AndTheJobIsScheduled()
    {
        var id = await NewJobAsync();

        var response = await ScheduleAsync(A, id, "2026-11-10T09:00:00+01:00", 90);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var job = await GetJobJsonAsync(A, id);
        Assert.Equal("Scheduled", job.GetProperty("status").GetString());
        Assert.Equal("2026-11-10T08:00:00Z", job.GetProperty("scheduledDate").GetString());
        Assert.Equal(90, job.GetProperty("estimatedDurationMinutes").GetInt32());
    }

    [Fact]
    public async Task TheListToo_ReturnsTheDateWithTheZ()
    {
        var id = await NewJobAsync();
        await ScheduleAsync(A, id, "2026-11-10T09:00:00+01:00");

        var list = await A.GetFromJsonAsync<JsonElement>("/api/v1/jobs");

        var job = list.EnumerateArray().Single(j => j.GetProperty("id").GetGuid() == id);
        Assert.Equal("2026-11-10T08:00:00Z", job.GetProperty("scheduledDate").GetString());
    }

    [Fact]
    public async Task ADateWithTheZ_AndAFractionOfASecondFromTheBrowser_IsAccepted()
    {
        var id = await NewJobAsync();

        var response = await ScheduleAsync(A, id, "2026-11-10T08:00:00.000Z", 30);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("2026-11-10T08:00:00Z", (await GetJobJsonAsync(A, id)).GetProperty("scheduledDate").GetString());
    }

    [Fact]
    public async Task Reschedule_WithoutDuration_ReplacesTheDate_AndErasesTheOldDuration()
    {
        var id = await NewJobAsync();
        await ScheduleAsync(A, id, "2026-11-10T09:00:00+01:00", 60);

        var response = await ScheduleAsync(A, id, "2026-11-12T15:00:00Z");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var job = await GetJobJsonAsync(A, id);
        Assert.Equal("Scheduled", job.GetProperty("status").GetString());
        Assert.Equal("2026-11-12T15:00:00Z", job.GetProperty("scheduledDate").GetString());
        Assert.Equal(JsonValueKind.Null, job.GetProperty("estimatedDurationMinutes").ValueKind);
    }

    [Fact]
    public async Task Unschedule_GoesBackToNew_WithoutDateOrDuration()
    {
        var id = await NewJobAsync();
        await ScheduleAsync(A, id, "2026-11-10T09:00:00+01:00", 60);

        var response = await A.DeleteAsync($"/api/v1/jobs/{id}/schedule");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var job = await GetJobJsonAsync(A, id);
        Assert.Equal("New", job.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, job.GetProperty("scheduledDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, job.GetProperty("estimatedDurationMinutes").ValueKind);
    }

    [Fact]
    public async Task AScheduledJob_CanBeStartedAndCompleted_AndKeepsItsDate()
    {
        var id = await NewJobAsync();
        await ScheduleAsync(A, id, "2026-11-10T09:00:00+01:00", 60);

        var start = await A.PostAsync($"/api/v1/jobs/{id}/start", content: null);
        var inProgress = await GetJobJsonAsync(A, id);
        var complete = await A.PostAsync($"/api/v1/jobs/{id}/complete", content: null);
        var completed = await GetJobJsonAsync(A, id);

        Assert.Equal(HttpStatusCode.NoContent, start.StatusCode);
        Assert.Equal("InProgress", inProgress.GetProperty("status").GetString());
        Assert.Equal("2026-11-10T08:00:00Z", inProgress.GetProperty("scheduledDate").GetString());
        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);
        Assert.Equal("Completed", completed.GetProperty("status").GetString());
        Assert.Equal("2026-11-10T08:00:00Z", completed.GetProperty("scheduledDate").GetString());
    }

    [Fact]
    public async Task AJobThatWasNeverScheduled_CanStillBeStarted()
    {
        var id = await NewJobAsync();

        var start = await A.PostAsync($"/api/v1/jobs/{id}/start", content: null);

        Assert.Equal(HttpStatusCode.NoContent, start.StatusCode);
        Assert.Equal("InProgress", (await GetJobJsonAsync(A, id)).GetProperty("status").GetString());
    }

    // ---- Entradas inválidas: un 400 de campo, y el trabajo no cambia ----

    [Theory]
    [InlineData("2026-11-10T09:00:00")]            // sin desfase: no se adivina el huso
    [InlineData("2026-11-10")]
    [InlineData("mañana a las nueve")]
    [InlineData("")]
    [InlineData("1999-12-31T23:59:59Z")]           // antes de la cota
    [InlineData("2100-01-01T00:00:00Z")]           // en la cota superior, que está fuera
    public async Task AnInvalidDate_Gets400WithTheFieldAndItsCode_AndTheJobDoesNotChange(string date)
    {
        var id = await NewJobAsync();

        var response = await ScheduleAsync(A, id, date, 30);
        var problem = await Dictionary.ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        var error = problem.GetProperty("errors").EnumerateArray().Single(e => e.GetProperty("field").GetString() == "ScheduledDate");
        Assert.Equal(JobErrors.ScheduleDateInvalid, error.GetProperty("code").GetString());
        Assert.Equal(Dictionary.Spanish(JobErrors.ScheduleDateInvalid), error.GetProperty("message").GetString());
        var job = await GetJobJsonAsync(A, id);
        Assert.Equal("New", job.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, job.GetProperty("scheduledDate").ValueKind);
    }

    [Fact]
    public async Task AMissingDate_Gets400_NotA500()
    {
        var id = await NewJobAsync();

        var response = await A.PutAsync($"/api/v1/jobs/{id}/schedule", Json("{}"));
        var problem = await Dictionary.ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        Assert.Contains(problem.GetProperty("errors").EnumerateArray(), e => e.GetProperty("field").GetString() == "ScheduledDate");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1441)]
    public async Task AnInvalidDuration_Gets400WithTheFieldAndItsCode_AndTheJobDoesNotChange(int minutes)
    {
        var id = await NewJobAsync();

        var response = await ScheduleAsync(A, id, "2026-11-10T09:00:00Z", minutes);
        var problem = await Dictionary.ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = problem.GetProperty("errors").EnumerateArray().Single(e => e.GetProperty("field").GetString() == "EstimatedDurationMinutes");
        Assert.Equal(JobErrors.EstimatedDurationInvalid, error.GetProperty("code").GetString());
        Assert.Equal("New", (await GetJobJsonAsync(A, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task ADurationThatIsNotANumber_Gets400_NotA500()
    {
        var id = await NewJobAsync();

        var response = await A.PutAsync($"/api/v1/jobs/{id}/schedule", Json("""{"scheduledDate":"2026-11-10T09:00:00Z","estimatedDurationMinutes":"mucho"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task APastDate_IsAccepted_SpecDecisionD5()
    {
        var id = await NewJobAsync();

        var response = await ScheduleAsync(A, id, "2020-03-01T10:00:00Z");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ---- Estados que no admiten la operación ----

    [Theory]
    [InlineData("start")]
    [InlineData("complete")]
    [InlineData("cancel")]
    public async Task ScheduleOfAJobThatIsInProgressCompletedOrCancelled_Gets400WithItsCode(string route)
    {
        var id = await NewJobAsync();
        // Estados: start → InProgress; complete → Completed (pasando por InProgress); cancel → Cancelled.
        if (route == "complete")
        {
            await A.PostAsync($"/api/v1/jobs/{id}/start", content: null);
        }

        await A.PostAsync($"/api/v1/jobs/{id}/{route}", content: null);
        var stateBefore = (await GetJobJsonAsync(A, id)).GetProperty("status").GetString();

        var response = await ScheduleAsync(A, id, "2026-11-10T09:00:00Z", 30);
        var problem = await Dictionary.ReadProblemAsync(response);

        Assert.NotEqual("New", stateBefore);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(JobErrors.CannotSchedule, problem.GetProperty("code").GetString());
        Assert.Equal(Dictionary.Spanish(JobErrors.CannotSchedule), problem.GetProperty("detail").GetString());
        var job = await GetJobJsonAsync(A, id);
        Assert.Equal(stateBefore, job.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, job.GetProperty("scheduledDate").ValueKind);
    }

    [Fact]
    public async Task UnscheduleOfAJobThatIsNotScheduled_Gets400WithItsCode()
    {
        var id = await NewJobAsync();

        var response = await A.DeleteAsync($"/api/v1/jobs/{id}/schedule");
        var problem = await Dictionary.ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(JobErrors.CannotUnschedule, problem.GetProperty("code").GetString());
        Assert.Equal(Dictionary.Spanish(JobErrors.CannotUnschedule), problem.GetProperty("detail").GetString());
    }

    [Fact]
    public void TheMessageOfCannotStart_NowSaysThatAScheduledJobCanBeStartedToo()
    {
        Assert.Contains("programado", Dictionary.Spanish(JobErrors.CannotStart));
    }

    // ---- Aislamiento entre empresas (R13) ----

    [Fact]
    public async Task AnotherCompany_CannotScheduleOrUnscheduleMyJob_ItGets404JustLikeAnIdThatDoesNotExist()
    {
        var id = await NewJobAsync();
        await ScheduleAsync(A, id, "2026-11-10T09:00:00Z", 60);

        var schedule = await ScheduleAsync(B, id, "2030-01-01T00:00:00Z", 10);
        var unschedule = await B.DeleteAsync($"/api/v1/jobs/{id}/schedule");
        var nonexistent = await ScheduleAsync(B, Guid.NewGuid(), "2030-01-01T00:00:00Z", 10);

        Assert.Equal(HttpStatusCode.NotFound, schedule.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unschedule.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nonexistent.StatusCode);
        Assert.Equal(JobErrors.NotFound, (await Dictionary.ReadProblemAsync(schedule)).GetProperty("code").GetString());
        // Y mi trabajo sigue exactamente como estaba.
        var job = await GetJobJsonAsync(A, id);
        Assert.Equal("Scheduled", job.GetProperty("status").GetString());
        Assert.Equal("2026-11-10T09:00:00Z", job.GetProperty("scheduledDate").GetString());
        Assert.Equal(60, job.GetProperty("estimatedDurationMinutes").GetInt32());
    }

    [Fact]
    public async Task WithoutAToken_BothOperationsGet401()
    {
        var id = await NewJobAsync();

        var schedule = await fixture.Anonymous.PutAsJsonAsync($"/api/v1/jobs/{id}/schedule", new { scheduledDate = "2026-11-10T09:00:00Z" });
        var unschedule = await fixture.Anonymous.DeleteAsync($"/api/v1/jobs/{id}/schedule");

        Assert.Equal(HttpStatusCode.Unauthorized, schedule.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unschedule.StatusCode);
    }
}
