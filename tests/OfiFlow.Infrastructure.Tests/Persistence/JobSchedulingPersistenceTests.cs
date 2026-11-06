using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Jobs.Queries.GetJobs;
using OfiFlow.Domain.Jobs;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Persistence;

/// <summary>
/// La programación de un trabajo contra SQL Server real (spec 020, bloque 1). Lo que más importa es la <b>fecha</b>: SQL Server guarda un
/// <c>datetime2</c> sin zona y EF lo devuelve con <c>Kind = Unspecified</c>; si así llegara al JSON saldría <c>2026-11-10T08:00:00</c> sin la
/// «Z» y el navegador lo leería como hora local, justo el error silencioso de horas que esta spec evita. Además, el índice del rango.
/// </summary>
public class JobSchedulingPersistenceTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture _fixture = fixture;

    private static readonly DateTime Monday9 = new(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc);

    private ApplicationDbContext Open(Guid tenantId) => _fixture.CreateDbContext(tenantId);

    private async Task<Job> SeedScheduledAsync(Guid tenantId, DateTime? at, int? minutes = null)
    {
        var job = Job.Create(tenantId, Guid.NewGuid(), "Reparar fuga", null, JobPriority.Normal);
        if (at is { } date)
        {
            job.Schedule(date, minutes);
        }

        await using var db = Open(tenantId);
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        return job;
    }

    [Fact]
    public async Task TheScheduledDate_RoundTripsExactly_AndComesBackAsUtc()
    {
        var tenantId = Guid.NewGuid();
        var at = new DateTime(2026, 11, 9, 9, 30, 15, DateTimeKind.Utc).AddTicks(1_234_560);
        var job = await SeedScheduledAsync(tenantId, at, 90);

        await using var read = Open(tenantId);
        var loaded = await read.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);

        Assert.Equal(at, loaded.ScheduledDate);
        Assert.Equal(DateTimeKind.Utc, loaded.ScheduledDate!.Value.Kind);
        Assert.Equal(JobStatus.Scheduled, loaded.Status);
        Assert.Equal(90, loaded.EstimatedDurationMinutes);
    }

    [Fact]
    public async Task TheDateInTheListDto_CarriesUtcToo_SoTheJsonEndsInZ()
    {
        var tenantId = Guid.NewGuid();
        var job = await SeedScheduledAsync(tenantId, Monday9);

        await using var read = Open(tenantId);
        var dtos = await new GetJobsQueryHandler(read).Handle(new GetJobsQuery(), CancellationToken.None);

        var dto = Assert.Single(dtos, d => d.Id == job.Id);
        Assert.Equal(Monday9, dto.ScheduledDate);
        Assert.Equal(DateTimeKind.Utc, dto.ScheduledDate!.Value.Kind);
        Assert.EndsWith("Z", System.Text.Json.JsonSerializer.Deserialize<string>(System.Text.Json.JsonSerializer.Serialize(dto.ScheduledDate)) ?? string.Empty);
    }

    [Fact]
    public async Task AJobWithoutSchedule_StoresNulls_NotAYearOneDate()
    {
        var tenantId = Guid.NewGuid();
        var job = await SeedScheduledAsync(tenantId, at: null);

        await using var read = Open(tenantId);
        var loaded = await read.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);

        Assert.Null(loaded.ScheduledDate);
        Assert.Null(loaded.EstimatedDurationMinutes);
    }

    [Fact]
    public async Task Unscheduling_ClearsTheColumnsInTheDatabase()
    {
        var tenantId = Guid.NewGuid();
        var job = await SeedScheduledAsync(tenantId, Monday9, 60);

        await using (var db = Open(tenantId))
        {
            var tracked = await db.Jobs.SingleAsync(j => j.Id == job.Id);
            tracked.Unschedule();
            await db.SaveChangesAsync();
        }

        await using var read = Open(tenantId);
        var loaded = await read.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        Assert.Equal(JobStatus.New, loaded.Status);
        Assert.Null(loaded.ScheduledDate);
        Assert.Null(loaded.EstimatedDurationMinutes);
    }

    [Fact]
    public async Task TheModel_HasTheTenantAndDateIndex_InThatOrder()
    {
        await using var db = Open(Guid.NewGuid());
        var entity = db.Model.FindEntityType(typeof(Job))!;

        var indexes = entity.GetIndexes().Select(i => string.Join("+", i.Properties.Select(p => p.Name))).ToList();

        Assert.Contains("TenantId+ScheduledDate", indexes);
    }

    [Fact]
    public async Task TheRealDatabase_HasTheIndex_WithTheTenantFirst()
    {
        await using var db = Open(Guid.NewGuid());

        var columns = await db.Database.SqlQuery<string>($"""
            SELECT c.name AS [Value]
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.name = N'IX_Jobs_TenantId_ScheduledDate'
            ORDER BY ic.key_ordinal
            """).ToListAsync();

        Assert.Equal(["TenantId", "ScheduledDate"], columns);
    }

    [Fact]
    public async Task ARangeQuery_OnlySeesTheOwnTenantsJobs()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var mine = await SeedScheduledAsync(tenantA, Monday9);
        var theirs = await SeedScheduledAsync(tenantB, Monday9);

        await using var read = Open(tenantA);
        var ids = await read.Jobs.AsNoTracking()
            .Where(j => j.ScheduledDate >= Monday9.AddDays(-1) && j.ScheduledDate < Monday9.AddDays(1))
            .Select(j => j.Id)
            .ToListAsync();

        Assert.Contains(mine.Id, ids);
        Assert.DoesNotContain(theirs.Id, ids);
    }
}
