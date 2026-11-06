using OfiFlow.Application.Jobs;
using OfiFlow.Application.Jobs.Queries.GetAgenda;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Application.Tests.Jobs;

/// <summary>
/// La agenda (spec 020 R11): qué trabajos salen de un rango y en qué orden. El rango es semiabierto [from, to): el instante exacto del
/// final no entra y el del principio sí. El aislamiento entre empresas va contra SQL Server real (AgendaPersistenceTests) y por HTTP.
/// </summary>
public class GetAgendaQueryHandlerTests
{
    // La semana de Madrid del lunes 9 de noviembre de 2026 (UTC+1): de las 23:00Z del domingo a las 23:00Z del domingo siguiente.
    private const string From = "2026-11-08T23:00:00Z";
    private const string To = "2026-11-15T23:00:00Z";

    private static readonly DateTime Monday9 = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);

    private static Job NewJob(string title = "Reparar fuga") => Job.Create(Guid.NewGuid(), Guid.NewGuid(), title, null, JobPriority.Normal);

    private static Job Scheduled(DateTime at, string title = "Reparar fuga", int? minutes = null)
    {
        var job = NewJob(title);
        job.Schedule(at, minutes);
        return job;
    }

    private static async Task<TestDbContext> SeedAsync(params Job[] jobs)
    {
        var db = TestDbContextFactory.Create();
        db.Jobs.AddRange(jobs);
        await db.SaveChangesAsync(CancellationToken.None);

        return db;
    }

    private static Task<List<JobDto>> AskAsync(TestDbContext db, string from = From, string to = To) =>
        new GetAgendaQueryHandler(db).Handle(new GetAgendaQuery(from, to), CancellationToken.None);

    [Fact]
    public async Task ReturnsTheJobsOfTheRange_OrderedByDate()
    {
        var thursday = Scheduled(Monday9.AddDays(3).AddHours(1), "Jueves");
        var mondayAfternoon = Scheduled(Monday9.AddHours(6), "Lunes tarde");
        var mondayMorning = Scheduled(Monday9, "Lunes mañana");
        await using var db = await SeedAsync(thursday, mondayAfternoon, mondayMorning);

        var agenda = await AskAsync(db);

        Assert.Equal(["Lunes mañana", "Lunes tarde", "Jueves"], agenda.Select(j => j.Title));
    }

    [Fact]
    public async Task WithManyJobsInsertedInAnyOrder_TheyComeBackByDate_NotByIdOrInsertion()
    {
        // Los Id son aleatorios: con tres trabajos, ordenar por Id coincide con ordenar por fecha una vez de cada seis, y un orden roto podría
        // pasar por casualidad. Con doce, coincidir por azar es 1 entre 479 millones. Se insertan en un desorden fijo (siempre el mismo).
        var inOrder = Enumerable.Range(0, 12).Select(i => Scheduled(Monday9.AddMinutes(17 * i), $"T{i:00}")).ToList();
        var shuffled = inOrder.OrderBy(job => job.Title.GetHashCode(StringComparison.Ordinal) % 7).ThenByDescending(job => job.Title, StringComparer.Ordinal).ToList();
        Assert.NotEqual(inOrder.Select(j => j.Title), shuffled.Select(j => j.Title)); // el desorden es real
        await using var db = await SeedAsync([.. shuffled]);

        var agenda = await AskAsync(db);

        Assert.Equal(inOrder.Select(j => j.Title), agenda.Select(j => j.Title));
        Assert.Equal(agenda.Select(j => j.ScheduledDate).Order(), agenda.Select(j => j.ScheduledDate));
    }

    [Fact]
    public async Task TheStartInstant_IsIncluded()
    {
        var atTheStart = Scheduled(new DateTime(2026, 11, 8, 23, 0, 0, DateTimeKind.Utc), "Justo al empezar");
        await using var db = await SeedAsync(atTheStart);

        var agenda = await AskAsync(db);

        Assert.Equal(["Justo al empezar"], agenda.Select(j => j.Title));
    }

    [Fact]
    public async Task TheEndInstant_IsExcluded_ItBelongsToTheNextRange()
    {
        var atTheEnd = Scheduled(new DateTime(2026, 11, 15, 23, 0, 0, DateTimeKind.Utc), "Justo al terminar");
        var oneSecondBefore = Scheduled(new DateTime(2026, 11, 15, 22, 59, 59, DateTimeKind.Utc), "Un segundo antes");
        await using var db = await SeedAsync(atTheEnd, oneSecondBefore);

        var agenda = await AskAsync(db);

        Assert.Equal(["Un segundo antes"], agenda.Select(j => j.Title));
    }

    [Fact]
    public async Task AJobOneSecondBeforeTheStart_IsNotIncluded()
    {
        var before = Scheduled(new DateTime(2026, 11, 8, 22, 59, 59, DateTimeKind.Utc), "Un segundo antes del principio");
        await using var db = await SeedAsync(before);

        Assert.Empty(await AskAsync(db));
    }

    [Fact]
    public async Task TheNextWeeksFirstJob_IsTheOtherRanges_NeverInBoth()
    {
        var boundary = Scheduled(new DateTime(2026, 11, 15, 23, 0, 0, DateTimeKind.Utc), "Frontera");
        await using var db = await SeedAsync(boundary);

        var thisWeek = await AskAsync(db, From, To);
        var nextWeek = await AskAsync(db, To, "2026-11-22T23:00:00Z");

        Assert.Empty(thisWeek);
        Assert.Equal(["Frontera"], nextWeek.Select(j => j.Title));
    }

    [Fact]
    public async Task ScheduledInProgressAndCompleted_AreShown_CancelledAndNewAreNot()
    {
        var scheduled = Scheduled(Monday9, "Programado");

        var inProgress = Scheduled(Monday9.AddHours(1), "En curso");
        inProgress.Start();

        var completed = Scheduled(Monday9.AddHours(2), "Completado");
        completed.Start();
        completed.Complete();

        var cancelled = Scheduled(Monday9.AddHours(3), "Cancelado");
        cancelled.Cancel(); // conserva la fecha, pero no sale

        var neverScheduled = NewJob("Sin fecha");

        await using var db = await SeedAsync(scheduled, inProgress, completed, cancelled, neverScheduled);

        var agenda = await AskAsync(db);

        Assert.Equal(["Programado", "En curso", "Completado"], agenda.Select(j => j.Title));
        Assert.Equal(["Scheduled", "InProgress", "Completed"], agenda.Select(j => j.Status));
    }

    [Fact]
    public async Task WhenTwoJobsStartAtTheSameInstant_TheOrderIsStable_ByIdThenNothingElse()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Scheduled(Monday9, "A la vez")).ToArray();
        await using var db = await SeedAsync(ids);

        var first = await AskAsync(db);
        var second = await AskAsync(db);

        Assert.Equal(first.Select(j => j.Id), second.Select(j => j.Id));
        Assert.Equal(ids.Select(j => j.Id).Order(), first.Select(j => j.Id));
    }

    [Fact]
    public async Task EachDto_CarriesTheScheduleAndTheRestOfTheJob()
    {
        var job = Scheduled(Monday9, "Revisar caldera", 90);
        var tenantUserId = Guid.NewGuid();
        job.AssignTo(tenantUserId);
        await using var db = await SeedAsync(job);

        var dto = Assert.Single(await AskAsync(db));

        Assert.Equal(job.Id, dto.Id);
        Assert.Equal(job.CustomerId, dto.CustomerId);
        Assert.Equal("Revisar caldera", dto.Title);
        Assert.Equal("Scheduled", dto.Status);
        Assert.Equal("Normal", dto.Priority);
        Assert.Equal(Monday9, dto.ScheduledDate);
        Assert.Equal(90, dto.EstimatedDurationMinutes);
        Assert.Equal(tenantUserId, dto.AssignedTenantUserId);
    }

    [Fact]
    public async Task TheEndpointsCanCarryOffsets_TheyAreComparedAsInstants()
    {
        var job = Scheduled(Monday9, "Lunes 09:00 en Madrid");
        await using var db = await SeedAsync(job);

        // La misma semana pedida con el desfase de Madrid.
        var agenda = await AskAsync(db, "2026-11-09T00:00:00+01:00", "2026-11-16T00:00:00+01:00");

        Assert.Equal(["Lunes 09:00 en Madrid"], agenda.Select(j => j.Title));
    }

    [Fact]
    public async Task AnEmptyRange_ReturnsAnEmptyList_NotAnError()
    {
        await using var db = await SeedAsync(Scheduled(Monday9.AddDays(30)));

        Assert.Empty(await AskAsync(db));
    }

    [Fact]
    public async Task WithoutPassingThroughTheValidator_ARangeWithoutOffset_IsRefused()
    {
        await using var db = await SeedAsync(Scheduled(Monday9));

        await Assert.ThrowsAsync<FormatException>(() => AskAsync(db, "2026-11-09T00:00:00", To));
        await Assert.ThrowsAsync<FormatException>(() => AskAsync(db, From, "2026-11-16"));
    }
}
