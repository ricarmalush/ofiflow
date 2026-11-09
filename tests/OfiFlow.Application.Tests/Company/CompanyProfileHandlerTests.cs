using System.Reflection;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Company;
using OfiFlow.Application.Company.Commands.UpsertCompanyProfile;
using OfiFlow.Application.Company.Queries.GetCompanyProfile;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Company;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Company;

/// <summary>
/// Los casos de uso del perfil fiscal (spec 024), con dobles: crear la primera vez, sustituir después, la carrera de dos altas, y que el registro lleve <b>quién y cuándo</b>
/// pero nunca un dato del perfil (R10). El filtro por empresa y el índice único real se prueban contra SQL Server en Infrastructure.
/// </summary>
public class CompanyProfileHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private const string LegalName = "Marta Gil Pérez";
    private const string TaxId = "12345678Z";
    private const string AddressLine = "Calle Secreta 17, 3.º B";
    private const string PostalCode = "28001";
    private const string City = "Madrid";
    private const string Province = "Madrid";
    private const string Phone = "+34 611 222 333";
    private const string Email = "marta.secreta@example.com";

    private static UpsertCompanyProfileCommand Command(
        string legalName = LegalName,
        string taxId = TaxId,
        string addressLine = AddressLine,
        string postalCode = PostalCode,
        string city = City,
        string? province = Province,
        string? phone = Phone,
        string? email = Email) =>
        new(legalName, taxId, addressLine, postalCode, city, province, phone, email);

    private static (UpsertCompanyProfileCommandHandler Handler, ListLogger<UpsertCompanyProfileCommandHandler> Log) HandlerFor(TestDbContext db)
    {
        var log = new ListLogger<UpsertCompanyProfileCommandHandler>();
        return (new UpsertCompanyProfileCommandHandler(db, new FakeTenantContext(TenantId), new FakeCurrentUser(TenantRole.Owner, UserId, TenantId), log), log);
    }

    private static async Task<List<CompanyProfile>> AllProfilesAsync(TestDbContext db)
    {
        await using var other = TestDbContextFactory.CreateSharing(db);
        return await other.CompanyProfiles.AsNoTracking().ToListAsync();
    }

    // ---------- Crear (la primera vez) ----------

    [Fact]
    public async Task TheFirstSave_CreatesTheProfile_ForTheCompanyOfTheToken_AndSavesOnce()
    {
        var db = TestDbContextFactory.Create();
        var (handler, _) = HandlerFor(db);

        await handler.Handle(Command(), CancellationToken.None);

        var profile = Assert.Single(await AllProfilesAsync(db));
        Assert.Equal(TenantId, profile.TenantId);
        Assert.Equal(LegalName, profile.LegalName);
        Assert.Equal(TaxId, profile.TaxId.Value);
        Assert.Equal(AddressLine, profile.AddressLine);
        Assert.Equal(PostalCode, profile.PostalCode.Value);
        Assert.Equal(City, profile.City);
        Assert.Equal(Province, profile.Province);
        Assert.Equal(Phone, profile.Phone!.Value);
        Assert.Equal(Email, profile.Email!.Value);
        Assert.Equal(1, db.SaveCount);
    }

    [Fact]
    public async Task TheTaxId_IsStoredNormalized_AndEveryTextTrimmed()
    {
        var db = TestDbContextFactory.Create();
        var (handler, _) = HandlerFor(db);

        await handler.Handle(Command(legalName: "  Marta Gil  ", taxId: " 12.345.678-z ", addressLine: " Calle 1 ", postalCode: " 28001 ", city: " Madrid ", province: "  ", phone: " 600123456 ", email: " a@example.com "), CancellationToken.None);

        var profile = Assert.Single(await AllProfilesAsync(db));
        Assert.Equal("Marta Gil", profile.LegalName);
        Assert.Equal("12345678Z", profile.TaxId.Value);
        Assert.Equal("Calle 1", profile.AddressLine);
        Assert.Equal("28001", profile.PostalCode.Value);
        Assert.Equal("Madrid", profile.City);
        Assert.Null(profile.Province);
        Assert.Equal("600123456", profile.Phone!.Value);
        Assert.Equal("a@example.com", profile.Email!.Value);
    }

    [Fact]
    public async Task TheOptionalFields_MayBeAbsentOrBlank()
    {
        var db = TestDbContextFactory.Create();
        var (handler, _) = HandlerFor(db);

        await handler.Handle(Command(province: null, phone: "   ", email: ""), CancellationToken.None);

        var profile = Assert.Single(await AllProfilesAsync(db));
        Assert.Null(profile.Province);
        Assert.Null(profile.Phone);
        Assert.Null(profile.Email);
    }

    // ---------- Sustituir ----------

    [Fact]
    public async Task ASecondSave_ReplacesTheProfile_InsteadOfCreatingAnother()
    {
        var db = TestDbContextFactory.Create();
        var (handler, _) = HandlerFor(db);
        await handler.Handle(Command(), CancellationToken.None);
        var id = Assert.Single(await AllProfilesAsync(db)).Id;
        db.ResetSaveCount();

        await handler.Handle(Command(legalName: "Fontanería Marta S.L.", taxId: "A58818501", addressLine: "Avenida Diagonal 5", postalCode: "08001", city: "Barcelona", province: "Barcelona", phone: "930000000", email: "info@example.com"), CancellationToken.None);

        var profile = Assert.Single(await AllProfilesAsync(db));
        Assert.Equal(id, profile.Id);
        Assert.Equal("Fontanería Marta S.L.", profile.LegalName);
        Assert.Equal("A58818501", profile.TaxId.Value);
        Assert.Equal("Avenida Diagonal 5", profile.AddressLine);
        Assert.Equal("08001", profile.PostalCode.Value);
        Assert.Equal("Barcelona", profile.City);
        Assert.Equal("Barcelona", profile.Province);
        Assert.Equal("930000000", profile.Phone!.Value);
        Assert.Equal("info@example.com", profile.Email!.Value);
        Assert.Equal(1, db.SaveCount);
    }

    [Fact]
    public async Task ASecondSave_WithoutTheOptionals_ErasesThePreviousOnes()
    {
        var db = TestDbContextFactory.Create();
        var (handler, _) = HandlerFor(db);
        await handler.Handle(Command(), CancellationToken.None);

        await handler.Handle(Command(province: null, phone: null, email: null), CancellationToken.None);

        var profile = Assert.Single(await AllProfilesAsync(db));
        Assert.Null(profile.Province);
        Assert.Null(profile.Phone);
        Assert.Null(profile.Email);
    }

    // ---------- La carrera de dos altas ----------

    [Fact]
    public async Task WhenAnotherRequestCreatedTheProfileFirst_ThisOneReplacesIt_NeverTwoProfilesNorAnError()
    {
        var db = TestDbContextFactory.Create();
        var (handler, log) = HandlerFor(db);

        // Entre la lectura (no había ninguno) y el guardado, otra petición de la misma empresa crea el suyo: el índice único hace fallar este guardado.
        db.ExceptionsOnNextSaves.Enqueue(new DbUpdateException("duplicate key"));
        db.OnQueuedFailure = () =>
        {
            using var winner = TestDbContextFactory.CreateSharing(db);
            winner.CompanyProfiles.Add(CompanyProfile.Create(TenantId, "Quien llegó antes", SpanishTaxId.Create("00000000T"), "Otra calle", SpanishPostalCode.Create("46001"), "Valencia", null, null, null));
            winner.SaveChangesAsync().GetAwaiter().GetResult();
        };

        await handler.Handle(Command(), CancellationToken.None);

        var profile = Assert.Single(await AllProfilesAsync(db));
        Assert.Equal(LegalName, profile.LegalName); // gana la última escritura (D9)
        Assert.Equal(TaxId, profile.TaxId.Value);
        Assert.Equal(TenantId, profile.TenantId);

        // Un solo evento, y como modificación: lo que quedó fue una modificación del que ganó.
        var entry = Assert.Single(log.Entries);
        Assert.Equal(SecurityEventIds.CompanyProfileChanged, entry.EventId.Id);
        Assert.Contains("creado False", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheInsertFailsForAnotherReason_TheErrorIsNotHidden_AndNothingIsLeftBehind()
    {
        var db = TestDbContextFactory.Create();
        var (handler, log) = HandlerFor(db);
        db.ExceptionsOnNextSaves.Enqueue(new DbUpdateException("connection lost")); // no hay ningún perfil ganador

        await Assert.ThrowsAsync<DbUpdateException>(() => handler.Handle(Command(), CancellationToken.None));

        Assert.Empty(await AllProfilesAsync(db));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task WhenTheRetryAlsoFails_TheErrorPropagates_AndNoEventIsLogged()
    {
        var db = TestDbContextFactory.Create();
        var (handler, log) = HandlerFor(db);
        db.ExceptionsOnNextSaves.Enqueue(new DbUpdateException("duplicate key"));
        db.ExceptionsOnNextSaves.Enqueue(new DbUpdateException("timeout"));
        db.OnQueuedFailure = () =>
        {
            using var winner = TestDbContextFactory.CreateSharing(db);
            if (!winner.CompanyProfiles.Any())
            {
                winner.CompanyProfiles.Add(CompanyProfile.Create(TenantId, "Quien llegó antes", SpanishTaxId.Create("00000000T"), "Otra calle", SpanishPostalCode.Create("46001"), "Valencia", null, null, null));
                winner.SaveChangesAsync().GetAwaiter().GetResult();
            }
        };

        await Assert.ThrowsAsync<DbUpdateException>(() => handler.Handle(Command(), CancellationToken.None));

        Assert.Empty(log.Entries);
        Assert.Equal("Quien llegó antes", Assert.Single(await AllProfilesAsync(db)).LegalName); // lo del ganador queda intacto
    }

    // ---------- Lo que nunca llega a la base de datos ----------

    [Fact]
    public async Task AnInvalidTaxId_OrPostalCode_ThatSkippedTheValidator_StillRaisesItsCode_AndSavesNothing()
    {
        var db = TestDbContextFactory.Create();
        var (handler, log) = HandlerFor(db);

        var taxError = await Assert.ThrowsAsync<DomainException>(() => handler.Handle(Command(taxId: "12345678A"), CancellationToken.None));
        var postalError = await Assert.ThrowsAsync<DomainException>(() => handler.Handle(Command(postalCode: "99999"), CancellationToken.None));

        Assert.Equal(CompanyErrors.TaxIdInvalid, taxError.Code);
        Assert.Equal(CompanyErrors.PostalCodeInvalid, postalError.Code);
        Assert.Equal(0, db.SaveCount);
        Assert.Empty(await AllProfilesAsync(db));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task AnInvalidPhone_OrEmail_ThatSkippedTheValidator_StillRaisesItsCode()
    {
        var db = TestDbContextFactory.Create();
        var (handler, _) = HandlerFor(db);

        var phoneError = await Assert.ThrowsAsync<DomainException>(() => handler.Handle(Command(phone: "abc"), CancellationToken.None));
        var emailError = await Assert.ThrowsAsync<DomainException>(() => handler.Handle(Command(email: "no-es-un-email"), CancellationToken.None));

        Assert.Equal(CustomerErrors.PhoneInvalid, phoneError.Code);
        Assert.Equal(CommonErrors.EmailInvalid, emailError.Code);
        Assert.Equal(0, db.SaveCount);
    }

    // ---------- El registro: quién y cuándo, nunca qué ----------

    [Fact]
    public async Task TheFirstSave_LogsOneEvent1409_AsCreated_WithTheUserAndTheCompany()
    {
        var db = TestDbContextFactory.Create();
        var (handler, log) = HandlerFor(db);

        await handler.Handle(Command(), CancellationToken.None);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(1409, entry.EventId.Id);
        Assert.Equal(SecurityEventIds.CompanyProfileChanged, entry.EventId.Id);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, entry.Level);
        Assert.Contains(UserId.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.Contains(TenantId.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.Contains("creado True", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecondSave_LogsAnEventAsModified()
    {
        var db = TestDbContextFactory.Create();
        var (handler, log) = HandlerFor(db);
        await handler.Handle(Command(), CancellationToken.None);
        log.Entries.Clear();

        await handler.Handle(Command(legalName: "Otro nombre"), CancellationToken.None);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(1409, entry.EventId.Id);
        Assert.Contains("creado False", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoDataOfTheProfile_EverReachesTheLog_NotEvenTheOldValues()
    {
        var db = TestDbContextFactory.Create();
        var (handler, log) = HandlerFor(db);

        await handler.Handle(Command(), CancellationToken.None);
        await handler.Handle(Command(legalName: "Nombre Secreto Nuevo", taxId: "A58818501", addressLine: "Calle Reservada 99", phone: "699888777", email: "otra.secreta@example.com"), CancellationToken.None);

        Assert.Equal(2, log.Entries.Count);
        string[] secrets = [LegalName, TaxId, "A58818501", AddressLine, "Calle Reservada 99", Phone, "699888777", Email, "otra.secreta@example.com", "Nombre Secreto Nuevo", PostalCode, City];
        foreach (var entry in log.Entries)
        {
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, entry.Message, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void TheCommand_NeverPrintsItsContent_AndCarriesNoCompany()
    {
        var text = Command().ToString();

        Assert.Equal("UpsertCompanyProfileCommand", text);
        Assert.DoesNotContain(TaxId, text, StringComparison.Ordinal);
        Assert.DoesNotContain(AddressLine, text, StringComparison.Ordinal);

        // La empresa sale del token: el comando no tiene ninguna propiedad de empresa.
        Assert.DoesNotContain(typeof(UpsertCompanyProfileCommand).GetProperties(), property => property.Name.Contains("Tenant", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(GetCompanyProfileQuery).GetProperties(), property => property.Name.Contains("Tenant", StringComparison.OrdinalIgnoreCase));
    }

    // ---------- Permisos ----------

    [Fact]
    public void TheCommand_NeedsCompanyManage_AndTheQuery_CompanyRead()
    {
        Assert.Equal(Permission.CompanyManage, typeof(UpsertCompanyProfileCommand).GetCustomAttribute<RequiresPermissionAttribute>()!.Permission);
        Assert.Equal(Permission.CompanyRead, typeof(GetCompanyProfileQuery).GetCustomAttribute<RequiresPermissionAttribute>()!.Permission);
    }

    // ---------- Leer ----------

    [Fact]
    public async Task ACompanyWithoutProfile_GetsAnEmptyOne_NotAnError()
    {
        var db = TestDbContextFactory.Create();

        var dto = await new GetCompanyProfileQueryHandler(db).Handle(new GetCompanyProfileQuery(), CancellationToken.None);

        Assert.False(dto.IsComplete);
        Assert.Null(dto.LegalName);
        Assert.Null(dto.TaxId);
        Assert.Null(dto.AddressLine);
        Assert.Null(dto.PostalCode);
        Assert.Null(dto.City);
        Assert.Null(dto.Province);
        Assert.Null(dto.Phone);
        Assert.Null(dto.Email);
        Assert.Equal(CompanyProfileDto.Empty, dto);
    }

    [Fact]
    public async Task ASavedProfile_IsReadBack_NormalizedAndComplete()
    {
        var db = TestDbContextFactory.Create();
        await HandlerFor(db).Handler.Handle(Command(taxId: "12.345.678-z"), CancellationToken.None);

        var dto = await new GetCompanyProfileQueryHandler(TestDbContextFactory.CreateSharing(db)).Handle(new GetCompanyProfileQuery(), CancellationToken.None);

        Assert.True(dto.IsComplete);
        Assert.Equal(LegalName, dto.LegalName);
        Assert.Equal("12345678Z", dto.TaxId);
        Assert.Equal(AddressLine, dto.AddressLine);
        Assert.Equal(PostalCode, dto.PostalCode);
        Assert.Equal(City, dto.City);
        Assert.Equal(Province, dto.Province);
        Assert.Equal(Phone, dto.Phone);
        Assert.Equal(Email, dto.Email);
    }

    [Fact]
    public async Task AProfileWithoutOptionals_IsReadBackWithNulls_AndStillComplete()
    {
        var db = TestDbContextFactory.Create();
        await HandlerFor(db).Handler.Handle(Command(province: null, phone: null, email: null), CancellationToken.None);

        var dto = await new GetCompanyProfileQueryHandler(TestDbContextFactory.CreateSharing(db)).Handle(new GetCompanyProfileQuery(), CancellationToken.None);

        Assert.True(dto.IsComplete);
        Assert.Null(dto.Province);
        Assert.Null(dto.Phone);
        Assert.Null(dto.Email);
    }

    [Fact]
    public async Task TheQuery_ReadsWithoutTracking_AndNeverSaves()
    {
        var db = TestDbContextFactory.Create();
        await HandlerFor(db).Handler.Handle(Command(), CancellationToken.None);
        var reader = TestDbContextFactory.CreateSharing(db);

        await new GetCompanyProfileQueryHandler(reader).Handle(new GetCompanyProfileQuery(), CancellationToken.None);

        Assert.Empty(reader.ChangeTracker.Entries());
        Assert.Equal(0, reader.SaveCount);
    }

    [Fact]
    public void TheDto_NeverPrintsItsContent()
    {
        var dto = new CompanyProfileDto(LegalName, TaxId, AddressLine, PostalCode, City, Province, Phone, Email, IsComplete: true);

        Assert.Equal("CompanyProfileDto", dto.ToString());
    }
}
