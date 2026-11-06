using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Jobs.Queries.GetAgenda;
using OfiFlow.Domain.Jobs;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Persistence;

/// <summary>
/// La agenda contra SQL Server real (spec 020, bloque 2): que cada empresa ve solo la suya, que los bordes del rango semiabierto se
/// respetan en la base de datos y no solo en memoria, y que la consulta <b>usa el índice</b> (TenantId, ScheduledDate) en vez de
/// recorrer la tabla entera.
/// </summary>
public class AgendaPersistenceTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture _fixture = fixture;

    private static readonly DateTime Monday = new(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);

    private ApplicationDbContext Open(Guid tenantId) => _fixture.CreateDbContext(tenantId);

    private async Task<Job> SeedAsync(Guid tenantId, DateTime at, string title = "Trabajo", Action<Job>? afterSchedule = null)
    {
        var job = Job.Create(tenantId, Guid.NewGuid(), title, null, JobPriority.Normal);
        job.Schedule(at, 30);
        afterSchedule?.Invoke(job);

        await using var db = Open(tenantId);
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        return job;
    }

    private static async Task<List<Application.Jobs.JobDto>> AskAsync(ApplicationDbContext db, DateTime from, DateTime to) =>
        await new GetAgendaQueryHandler(db).Handle(new GetAgendaQuery(Iso(from), Iso(to)), CancellationToken.None);

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task EachCompany_OnlySeesItsOwnAgenda_EvenForTheSameInstants()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var mine = await SeedAsync(tenantA, Monday.AddHours(9), "De A");
        var theirs = await SeedAsync(tenantB, Monday.AddHours(9), "De B");

        await using var readA = Open(tenantA);
        await using var readB = Open(tenantB);
        var agendaA = await AskAsync(readA, Monday, Monday.AddDays(7));
        var agendaB = await AskAsync(readB, Monday, Monday.AddDays(7));

        Assert.Equal([mine.Id], agendaA.Select(j => j.Id));
        Assert.Equal([theirs.Id], agendaB.Select(j => j.Id));
    }

    [Fact]
    public async Task TheRangeIsHalfOpen_InTheDatabaseToo()
    {
        var tenantId = Guid.NewGuid();
        var atStart = await SeedAsync(tenantId, Monday, "Al empezar");
        var oneBefore = await SeedAsync(tenantId, Monday.AddSeconds(-1), "Un segundo antes");
        var atEnd = await SeedAsync(tenantId, Monday.AddDays(7), "Al terminar");
        var lastSecond = await SeedAsync(tenantId, Monday.AddDays(7).AddSeconds(-1), "Último segundo");

        await using var read = Open(tenantId);
        var agenda = await AskAsync(read, Monday, Monday.AddDays(7));

        Assert.Equal([atStart.Id, lastSecond.Id], agenda.Select(j => j.Id));
        Assert.DoesNotContain(agenda, j => j.Id == oneBefore.Id || j.Id == atEnd.Id);
    }

    [Fact]
    public async Task OnlyScheduledInProgressAndCompleted_AreReturned()
    {
        var tenantId = Guid.NewGuid();
        var scheduled = await SeedAsync(tenantId, Monday.AddHours(1), "Programado");
        var inProgress = await SeedAsync(tenantId, Monday.AddHours(2), "En curso", j => j.Start());
        var completed = await SeedAsync(tenantId, Monday.AddHours(3), "Completado", j => { j.Start(); j.Complete(); });
        await SeedAsync(tenantId, Monday.AddHours(4), "Cancelado", j => j.Cancel());

        await using var read = Open(tenantId);
        var agenda = await AskAsync(read, Monday, Monday.AddDays(7));

        Assert.Equal([scheduled.Id, inProgress.Id, completed.Id], agenda.Select(j => j.Id));
    }

    [Fact]
    public async Task TheDatesInTheAgenda_ComeBackAsUtc_SoTheJsonEndsInZ()
    {
        var tenantId = Guid.NewGuid();
        await SeedAsync(tenantId, Monday.AddHours(8));

        await using var read = Open(tenantId);
        var dto = Assert.Single(await AskAsync(read, Monday, Monday.AddDays(7)));

        Assert.Equal(DateTimeKind.Utc, dto.ScheduledDate!.Value.Kind);
        Assert.Equal(Monday.AddHours(8), dto.ScheduledDate);
    }

    [Fact]
    public async Task AJobWithoutDate_NeverAppears_EvenInAHugeRange()
    {
        var tenantId = Guid.NewGuid();
        await using (var db = Open(tenantId))
        {
            db.Jobs.Add(Job.Create(tenantId, Guid.NewGuid(), "Sin fecha", null, JobPriority.Normal));
            await db.SaveChangesAsync();
        }

        await using var read = Open(tenantId);
        Assert.Empty(await AskAsync(read, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2000, 2, 11, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task TheRangeQuery_UsesTheTenantAndDateIndex_WhenTheTableIsBig()
    {
        // Tabla grande y rango estrecho: 8.000 trabajos de una empresa repartidos en 400 días (20 al día) y un rango de un día (~20 filas).
        // Con pocas filas SQL Server hace bien en recorrer la tabla (el índice no es de cobertura: cada fila pide una búsqueda en la clave
        // primaria), así que lo que se comprueba no es «el índice siempre gana», sino que con datos suficientes la consulta REAL de la agenda
        // lo puede usar: que su forma (empresa primero, fecha después) casa con el índice.
        var tenantId = Guid.NewGuid();
        await using (var db = Open(tenantId))
        {
            var jobs = new List<Job>(8000);
            for (var day = 0; day < 400; day++)
            {
                for (var n = 0; n < 20; n++)
                {
                    var job = Job.Create(tenantId, Guid.NewGuid(), $"T{day}-{n}", null, JobPriority.Normal);
                    job.Schedule(Monday.AddDays(day).AddMinutes(n * 30), 30);
                    jobs.Add(job);
                }
            }

            db.Jobs.AddRange(jobs);
            await db.SaveChangesAsync();
        }

        await using var read = Open(tenantId);

        // Caché de planes vacía (es un contenedor propio de esta clase) para que el plan se compile AHORA, con los datos de esta tabla y con
        // los parámetros que EF envía de verdad, y no sea el que dejó otro test de la clase con la tabla casi vacía.
        await read.Database.ExecuteSqlRawAsync("DBCC FREEPROCCACHE");

        var from = Monday.AddDays(100);
        var to = Monday.AddDays(101);
        var ids = await new GetAgendaQueryHandler(read).Handle(new GetAgendaQuery(Iso(from), Iso(to)), CancellationToken.None);
        Assert.Equal(20, ids.Count); // sanity: devuelve lo esperado antes de mirar el plan

        var plan = await read.Database.SqlQuery<string>($"""
            SELECT TOP 1 CAST(qp.query_plan AS nvarchar(max)) AS [Value]
            FROM sys.dm_exec_cached_plans cp
            CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle) st
            CROSS APPLY sys.dm_exec_query_plan(cp.plan_handle) qp
            WHERE st.text LIKE N'%ScheduledDate%' AND st.text LIKE N'%ORDER BY%' AND st.text NOT LIKE N'%dm_exec_cached_plans%'
            ORDER BY cp.usecounts DESC
            """).FirstAsync();

        var operators = string.Join(", ", System.Text.RegularExpressions.Regex.Matches(plan, "PhysicalOp=\"([^\"]+)\"").Select(m => m.Groups[1].Value));
        Assert.True(plan.Contains("IX_Jobs_TenantId_ScheduledDate", StringComparison.Ordinal), $"Operadores del plan: {operators}");
        Assert.True(plan.Contains("PhysicalOp=\"Index Seek\"", StringComparison.Ordinal), $"Operadores del plan: {operators}"); // una búsqueda por rango
    }
}
