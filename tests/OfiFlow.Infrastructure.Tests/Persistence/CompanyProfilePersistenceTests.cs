using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Company;
using OfiFlow.Application.Company.Commands.UpsertCompanyProfile;
using OfiFlow.Application.Company.Queries.GetCompanyProfile;
using OfiFlow.Domain.Company;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Persistence.Interceptors;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Persistence;

/// <summary>
/// El perfil fiscal contra SQL Server real (spec 024, bloque 1): el filtro por empresa, el índice único por empresa, la ida y vuelta de cada dato, <b>dos altas simultáneas</b> (queda un
/// solo perfil y nadie recibe un error), que el NIF <b>no</b> es único entre empresas (D6) y que la lectura usa su índice.
/// </summary>
public class CompanyProfilePersistenceTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture _fixture = fixture;

    private ApplicationDbContext Open(Guid tenantId) => _fixture.CreateDbContext(tenantId);

    private static UpsertCompanyProfileCommand Command(string legalName = "Marta Gil Pérez", string taxId = "12345678Z", string? province = "Madrid", string? phone = "600123456", string? email = "marta@example.com") =>
        new(legalName, taxId, "Calle Mayor 1", "28001", "Madrid", province, phone, email);

    private async Task UpsertAsync(Guid tenantId, UpsertCompanyProfileCommand command)
    {
        // Con el interceptor de auditoría, como en la API real: es el que rellena CreatedAt y UpdatedAt (ADR-006).
        await using var db = _fixture.CreateDbContext(tenantId, builder => builder.AddInterceptors(new AuditableEntitySaveChangesInterceptor()));
        await new UpsertCompanyProfileCommandHandler(db, new FixedTenantContext(tenantId), new TestCurrentUser(TenantRole.Owner, Guid.NewGuid(), tenantId), new ListLogger<UpsertCompanyProfileCommandHandler>())
            .Handle(command, CancellationToken.None);
    }

    private async Task<CompanyProfileDto> ReadAsync(Guid tenantId)
    {
        await using var db = Open(tenantId);
        return await new GetCompanyProfileQueryHandler(db).Handle(new GetCompanyProfileQuery(), CancellationToken.None);
    }

    private async Task<int> RowsAsync(Guid tenantId)
    {
        await using var db = Open(tenantId);
        return await db.CompanyProfiles.CountAsync();
    }

    // ---------- Ida y vuelta ----------

    [Fact]
    public async Task EveryField_SurvivesTheRoundTrip_AndTheInstantsComeBackAsUtc()
    {
        var tenantId = Guid.NewGuid();
        await UpsertAsync(tenantId, Command(taxId: "x-1234567-l", phone: "+34 600 123 456"));

        await using var db = Open(tenantId);
        var stored = await db.CompanyProfiles.AsNoTracking().SingleAsync();

        Assert.Equal(tenantId, stored.TenantId);
        Assert.Equal("Marta Gil Pérez", stored.LegalName);
        Assert.Equal("X1234567L", stored.TaxId.Value);
        Assert.Equal("Calle Mayor 1", stored.AddressLine);
        Assert.Equal("28001", stored.PostalCode.Value);
        Assert.Equal("Madrid", stored.City);
        Assert.Equal("Madrid", stored.Province);
        Assert.Equal("+34 600 123 456", stored.Phone!.Value);
        Assert.Equal("marta@example.com", stored.Email!.Value);
        Assert.Equal(DateTimeKind.Utc, stored.CreatedAt.Kind);
        Assert.NotEqual(default, stored.CreatedAt);
        Assert.Null(stored.UpdatedAt);
    }

    [Fact]
    public async Task TheNormalizedTaxId_IsWhatIsStoredInTheColumn_NotWhatWasTyped()
    {
        var tenantId = Guid.NewGuid();
        await UpsertAsync(tenantId, Command(taxId: " 12.345.678-z "));

        await using var db = Open(tenantId);
        var raw = await db.Database.SqlQuery<string>($"SELECT [TaxId] AS [Value] FROM [CompanyProfiles] WHERE [TenantId] = {tenantId}").SingleAsync();

        Assert.Equal("12345678Z", raw);
    }

    [Fact]
    public async Task EveryTextAtItsLimit_IsStoredWhole()
    {
        var tenantId = Guid.NewGuid();
        var command = new UpsertCompanyProfileCommand(
            new string('a', CompanyProfile.LegalNameMaxLength), "12345678Z", new string('b', CompanyProfile.AddressLineMaxLength), "28001", new string('c', CompanyProfile.CityMaxLength),
            new string('d', CompanyProfile.ProvinceMaxLength), "+34 (600) 123-456-78", new string('e', Email.MaxLength - "@example.com".Length) + "@example.com");

        await UpsertAsync(tenantId, command);

        var dto = await ReadAsync(tenantId);
        Assert.Equal(CompanyProfile.LegalNameMaxLength, dto.LegalName!.Length);
        Assert.Equal(CompanyProfile.AddressLineMaxLength, dto.AddressLine!.Length);
        Assert.Equal(CompanyProfile.CityMaxLength, dto.City!.Length);
        Assert.Equal(CompanyProfile.ProvinceMaxLength, dto.Province!.Length);
        Assert.Equal(PhoneNumber.MaxLength, dto.Phone!.Length);
        Assert.Equal(Email.MaxLength, dto.Email!.Length);
    }

    [Fact]
    public async Task TheColumns_HaveTheLengthsOfTheDomainConstants()
    {
        await using var db = Open(Guid.NewGuid());
        var lengths = await db.Database.SqlQuery<ColumnLength>($"""
            SELECT COLUMN_NAME AS [Name], CHARACTER_MAXIMUM_LENGTH AS [Length]
            FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = N'CompanyProfiles' AND CHARACTER_MAXIMUM_LENGTH IS NOT NULL
            """).ToListAsync();
        var byName = lengths.ToDictionary(l => l.Name, l => l.Length);

        Assert.Equal(CompanyProfile.LegalNameMaxLength, byName["LegalName"]);
        Assert.Equal(SpanishTaxId.MaxLength, byName["TaxId"]);
        Assert.Equal(CompanyProfile.AddressLineMaxLength, byName["AddressLine"]);
        Assert.Equal(SpanishPostalCode.Length, byName["PostalCode"]);
        Assert.Equal(CompanyProfile.CityMaxLength, byName["City"]);
        Assert.Equal(CompanyProfile.ProvinceMaxLength, byName["Province"]);
        Assert.Equal(PhoneNumber.MaxLength, byName["Phone"]);
        Assert.Equal(Email.MaxLength, byName["Email"]);
    }

    private sealed record ColumnLength(string Name, int Length);

    // ---------- Sustituir ----------

    [Fact]
    public async Task ASecondSave_ReplacesTheRow_KeepsItsId_StampsUpdatedAt_AndErasesTheOptionals()
    {
        var tenantId = Guid.NewGuid();
        await UpsertAsync(tenantId, Command());
        Guid id;
        DateTime createdAt;
        await using (var db = Open(tenantId))
        {
            var first = await db.CompanyProfiles.AsNoTracking().SingleAsync();
            id = first.Id;
            createdAt = first.CreatedAt;
        }

        await UpsertAsync(tenantId, Command(legalName: "Fontanería Marta S.L.", taxId: "A58818501", province: null, phone: null, email: null));

        await using var read = Open(tenantId);
        var stored = await read.CompanyProfiles.AsNoTracking().SingleAsync();
        Assert.Equal(id, stored.Id);
        Assert.Equal("Fontanería Marta S.L.", stored.LegalName);
        Assert.Equal("A58818501", stored.TaxId.Value);
        Assert.Null(stored.Province);
        Assert.Null(stored.Phone);
        Assert.Null(stored.Email);
        Assert.Equal(createdAt, stored.CreatedAt);
        Assert.NotNull(stored.UpdatedAt);
        Assert.Equal(DateTimeKind.Utc, stored.UpdatedAt!.Value.Kind);
    }

    // ---------- El filtro por empresa ----------

    [Fact]
    public async Task EachCompany_OnlySeesItsOwnProfile_AndOneWithoutProfileSeesAnEmptyOne()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var tenantC = Guid.NewGuid();
        await UpsertAsync(tenantA, Command(legalName: "Empresa A"));
        await UpsertAsync(tenantB, Command(legalName: "Empresa B", taxId: "A58818501"));

        Assert.Equal("Empresa A", (await ReadAsync(tenantA)).LegalName);
        Assert.Equal("Empresa B", (await ReadAsync(tenantB)).LegalName);
        Assert.Equal(CompanyProfileDto.Empty, await ReadAsync(tenantC));

        await using var readA = Open(tenantA);
        Assert.Equal(1, await readA.CompanyProfiles.CountAsync());
        Assert.Empty(await readA.CompanyProfiles.Where(p => p.TenantId == tenantB).ToListAsync());
    }

    [Fact]
    public async Task SavingInOneCompany_NeverTouchesTheProfileOfAnother()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await UpsertAsync(tenantA, Command(legalName: "Empresa A"));

        await UpsertAsync(tenantB, Command(legalName: "Empresa B"));
        await UpsertAsync(tenantB, Command(legalName: "Empresa B otra vez"));

        Assert.Equal("Empresa A", (await ReadAsync(tenantA)).LegalName);
        Assert.Equal(1, await RowsAsync(tenantA));
        Assert.Equal(1, await RowsAsync(tenantB));
    }

    [Fact]
    public async Task TheProfile_IsStampedWithTheCompanyOfTheTokenAndNoOther()
    {
        var tenantId = Guid.NewGuid();
        await UpsertAsync(tenantId, Command());

        await using var db = Open(tenantId);
        var tenantIds = await db.Database.SqlQuery<Guid>($"SELECT [TenantId] AS [Value] FROM [CompanyProfiles] WHERE [TenantId] = {tenantId}").ToListAsync();

        Assert.Equal([tenantId], tenantIds);
    }

    // ---------- El índice único por empresa ----------

    [Fact]
    public async Task TheUniqueIndex_RefusesASecondProfileOfTheSameCompany_ButNotOneOfAnother()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await UpsertAsync(tenantA, Command());

        await using (var db = Open(tenantA))
        {
            db.CompanyProfiles.Add(CompanyProfile.Create(tenantA, "Otro", SpanishTaxId.Create("00000000T"), "Calle", SpanishPostalCode.Create("46001"), "Valencia", null, null, null));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        await using (var db = Open(tenantB))
        {
            db.CompanyProfiles.Add(CompanyProfile.Create(tenantB, "Otra empresa", SpanishTaxId.Create("00000000T"), "Calle", SpanishPostalCode.Create("46001"), "Valencia", null, null, null));
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await RowsAsync(tenantA));
        Assert.Equal(1, await RowsAsync(tenantB));
    }

    [Fact]
    public async Task TheTaxId_IsNotUniqueAcrossCompanies_SoNobodyCanProbeWhetherAnotherCompanyUsesIt()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await UpsertAsync(tenantA, Command(taxId: "12345678Z"));
        await UpsertAsync(tenantB, Command(taxId: "12345678Z")); // sin error ni distinta respuesta

        Assert.Equal("12345678Z", (await ReadAsync(tenantA)).TaxId);
        Assert.Equal("12345678Z", (await ReadAsync(tenantB)).TaxId);
    }

    [Fact]
    public async Task TheOnlyUniqueIndex_IsTheOneByCompany()
    {
        await using var db = Open(Guid.NewGuid());
        var indexes = await db.Database.SqlQuery<IndexInfo>($"""
            SELECT i.[name] AS [Name], i.[is_unique] AS [IsUnique],
                   (SELECT STRING_AGG(c.[name], N',') WITHIN GROUP (ORDER BY ic.[key_ordinal]) FROM sys.index_columns ic JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
                    WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]) AS [Columns]
            FROM sys.indexes i WHERE i.[object_id] = OBJECT_ID(N'CompanyProfiles') AND i.[is_primary_key] = 0
            """).ToListAsync();

        var index = Assert.Single(indexes);
        Assert.Equal("IX_CompanyProfiles_TenantId", index.Name);
        Assert.True(index.IsUnique);
        Assert.Equal("TenantId", index.Columns);
    }

    private sealed record IndexInfo(string Name, bool IsUnique, string Columns);

    // ---------- La carrera de dos altas ----------

    [Fact]
    public async Task SeveralFirstSavesAtTheSameTime_LeaveOneProfile_AndNoneFailsNorIsLost()
    {
        // Varias rondas, cada una en una empresa nueva: cuatro peticiones leen «no hay perfil» a la vez y compiten por insertarlo. Gana el índice único; las demás releen y sustituyen.
        for (var round = 0; round < 8; round++)
        {
            var tenantId = Guid.NewGuid();
            var names = Enumerable.Range(0, 4).Select(i => $"Intento {round}-{i}").ToArray();

            await Task.WhenAll(names.Select(name => Task.Run(() => UpsertAsync(tenantId, Command(legalName: name, taxId: "12345678Z")))));

            Assert.Equal(1, await RowsAsync(tenantId));
            var dto = await ReadAsync(tenantId);
            Assert.Contains(dto.LegalName, names); // gana la última escritura: uno de ellos, entero y coherente
            Assert.Equal("12345678Z", dto.TaxId);
            Assert.True(dto.IsComplete);
        }
    }

    [Fact]
    public async Task SeveralSavesAtTheSameTime_OnAnExistingProfile_LeaveOneCoherentProfile()
    {
        var tenantId = Guid.NewGuid();
        await UpsertAsync(tenantId, Command(legalName: "Original"));
        var commands = new[]
        {
            Command(legalName: "Uno", taxId: "12345678Z", province: "Madrid", phone: "600000001", email: "uno@example.com"),
            Command(legalName: "Dos", taxId: "A58818501", province: null, phone: null, email: null),
            Command(legalName: "Tres", taxId: "X1234567L", province: "Sevilla", phone: "600000003", email: "tres@example.com")
        };

        await Task.WhenAll(commands.Select(command => Task.Run(() => UpsertAsync(tenantId, command))));

        Assert.Equal(1, await RowsAsync(tenantId));
        var dto = await ReadAsync(tenantId);
        // Lo que quedó es UNO de los tres, entero: ningún campo es de otro (la escritura sustituye todo).
        var winner = Assert.Single(commands, c => c.LegalName == dto.LegalName);
        Assert.Equal(winner.TaxId, dto.TaxId);
        Assert.Equal(winner.Province, dto.Province);
        Assert.Equal(winner.Phone, dto.Phone);
        Assert.Equal(winner.Email, dto.Email);
    }

    // ---------- El plan de la lectura ----------

    [Fact]
    public async Task TheRead_SeeksByTheIndexOfTheCompany_WhenTheTableIsBig()
    {
        var target = Guid.NewGuid();
        await using (var db = Open(target))
        {
            var rows = new List<CompanyProfile>(3000);
            for (var t = 0; t < 3000; t++)
            {
                rows.Add(CompanyProfile.Create(t == 0 ? target : Guid.NewGuid(), $"Empresa {t}", SpanishTaxId.Create("12345678Z"), "Calle", SpanishPostalCode.Create("28001"), "Madrid", null, null, null));
            }

            db.CompanyProfiles.AddRange(rows);
            await db.SaveChangesAsync();
        }

        await using var read = Open(target);
        await read.Database.ExecuteSqlRawAsync("DBCC FREEPROCCACHE");
        var dto = await new GetCompanyProfileQueryHandler(read).Handle(new GetCompanyProfileQuery(), CancellationToken.None);
        Assert.Equal("Empresa 0", dto.LegalName);

        var plans = await read.Database.SqlQuery<string>($"""
            SELECT CAST(qp.query_plan AS nvarchar(max)) AS [Value]
            FROM sys.dm_exec_cached_plans cp
            CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle) st
            CROSS APPLY sys.dm_exec_query_plan(cp.plan_handle) qp
            WHERE st.text LIKE N'%FROM [[]CompanyProfiles]%' AND st.text NOT LIKE N'%dm_exec_cached_plans%'
            """).ToListAsync();

        var all = string.Join("\n", plans);
        Assert.True(all.Contains("IX_CompanyProfiles_TenantId", StringComparison.Ordinal), "La lectura del perfil no usa el índice por empresa");
        Assert.DoesNotContain("Table Scan", all, StringComparison.Ordinal);
    }
}
