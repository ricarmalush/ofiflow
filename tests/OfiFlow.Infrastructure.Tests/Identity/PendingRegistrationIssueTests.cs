using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Security;
using OfiFlow.Domain.Identity;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// Pedir un registro (spec 021, bloque 1) contra SQL Server real: crear el pendiente, anular el anterior, limitar por email y hora, y decir «ya tiene cuenta». Lo que se prueba es lo que decide
/// si el flujo se puede abusar: cada caso de «se crea» va con su control de «no se crea».
/// </summary>
public class PendingRegistrationIssueTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private static readonly DateTime Start = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerContainerFixture _fixture = fixture;

    private ApplicationDbContext Open() => _fixture.CreateDbContext(Guid.NewGuid());

    private static string UniqueEmail(string prefix = "persona") => $"{prefix}-{Guid.NewGuid():N}@example.com";

    private async Task AccountWithEmailAsync(string email)
    {
        await using var db = Open();
        db.ApplicationUsers.Add(new ApplicationUser(Guid.NewGuid(), email));
        await db.SaveChangesAsync();
    }

    private async Task<Guid> AccountAsync(string email)
    {
        var id = Guid.NewGuid();
        await using var db = Open();
        db.ApplicationUsers.Add(new ApplicationUser(id, email));
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<PendingRegistrationIssue> IssueAsync(string email, FakeTimeProvider time, string company = "Fontanería Demo SL", string person = "Ana Pérez", string terms = "2026-11")
    {
        await using var db = Open();
        return await new PendingRegistrationService(db, time).IssueAsync(email, company, person, terms, CancellationToken.None);
    }

    private async Task<List<PendingRegistration>> RowsOfAsync(string email)
    {
        var normalized = email.Trim().ToUpperInvariant();
        await using var db = Open();
        return await db.PendingRegistrations.AsNoTracking().Where(r => r.NormalizedEmail == normalized).OrderBy(r => r.CreatedAt).ToListAsync();
    }

    // ===== Un email nuevo =====

    [Fact]
    public async Task ANewEmail_IsIssued_WithATokenInClear_AndTheRowKeepsOnlyItsHash()
    {
        var email = UniqueEmail();

        var issue = await IssueAsync(email, new FakeTimeProvider(Start));

        Assert.Equal(PendingRegistrationIssueOutcome.Issued, issue.Outcome);
        Assert.True(OpaqueToken.IsWellFormed(issue.Token));
        var row = Assert.Single(await RowsOfAsync(email));
        Assert.Equal(OpaqueToken.Hash(issue.Token!), row.TokenHash);
        // El token NO está en ningún sitio de la fila, ni siquiera dentro de otro campo.
        Assert.All(new[] { row.Email, row.NormalizedEmail, row.CompanyName, row.UserName, row.TermsVersion, row.TokenHash }, value => Assert.DoesNotContain(issue.Token!, value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRow_KeepsWhatWasWritten_TheTermsAndTheDates()
    {
        var email = UniqueEmail();

        await IssueAsync(email, new FakeTimeProvider(Start), company: "Electricidad Gil SL", person: "Luis Gil", terms: "2026-11");

        var row = Assert.Single(await RowsOfAsync(email));
        Assert.Equal(email, row.Email);
        Assert.Equal("Electricidad Gil SL", row.CompanyName);
        Assert.Equal("Luis Gil", row.UserName);
        Assert.Equal("2026-11", row.TermsVersion);
        Assert.Equal(Start, row.TermsAcceptedAt);
        Assert.Equal(Start, row.CreatedAt);
        Assert.Equal(Start.AddHours(24), row.ExpiresAt);
        Assert.Null(row.UsedAt);
    }

    [Fact]
    public async Task TheEmail_IsTrimmed_AndStoredWithItsNormalizedFormInUppercase()
    {
        var email = UniqueEmail("MiXto");

        await IssueAsync($"  {email}  ", new FakeTimeProvider(Start));

        var row = Assert.Single(await RowsOfAsync(email));
        Assert.Equal(email, row.Email);
        Assert.Equal(email.ToUpperInvariant(), row.NormalizedEmail);
    }

    [Fact]
    public async Task TheSameEmailInAnotherCase_IsTheSameEmail()
    {
        var email = UniqueEmail("caso");
        await IssueAsync(email, new FakeTimeProvider(Start));

        await IssueAsync(email.ToUpperInvariant(), new FakeTimeProvider(Start.AddMinutes(1)));

        var rows = await RowsOfAsync(email);
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.UsedAt is null); // un solo vigente, aunque se escribiera distinto
    }

    // ===== Un email que ya tiene cuenta =====

    [Fact]
    public async Task AnEmailWithAnAccount_IsAccountExists_AndCreatesNothing()
    {
        var email = UniqueEmail("existe");
        await AccountWithEmailAsync(email);

        var issue = await IssueAsync(email, new FakeTimeProvider(Start));

        Assert.Equal(PendingRegistrationIssueOutcome.AccountExists, issue.Outcome);
        Assert.Null(issue.Token);
        Assert.Empty(await RowsOfAsync(email));
    }

    [Theory]
    [InlineData("Existe-{0}@Example.COM")]
    [InlineData("existe-{0}@example.com")]
    public async Task AnAccount_IsFoundWhateverTheCaseOfTheEmail(string template)
    {
        var token = Guid.NewGuid().ToString("N");
        await AccountWithEmailAsync(string.Format(template, token).ToLowerInvariant());

        var issue = await IssueAsync(string.Format(template, token), new FakeTimeProvider(Start));

        Assert.Equal(PendingRegistrationIssueOutcome.AccountExists, issue.Outcome);
    }

    [Fact]
    public async Task APlatformOperatorsEmail_IsAlsoAccountExists_ItOccupiesTheEmailAsAnyOther()
    {
        var email = UniqueEmail("operador");
        var userId = await AccountAsync(email);
        await using (var db = Open())
        {
            db.PlatformOperators.Add(PlatformOperator.Create(userId, Start));
            await db.SaveChangesAsync();
        }

        var issue = await IssueAsync(email, new FakeTimeProvider(Start));

        Assert.Equal(PendingRegistrationIssueOutcome.AccountExists, issue.Outcome);
        Assert.Empty(await RowsOfAsync(email));
    }

    [Fact]
    public async Task AnotherPersonsAccount_DoesNotBlockANewEmail()
    {
        await AccountWithEmailAsync(UniqueEmail("otra"));

        var issue = await IssueAsync(UniqueEmail("nueva"), new FakeTimeProvider(Start));

        Assert.Equal(PendingRegistrationIssueOutcome.Issued, issue.Outcome);
    }

    // ===== Un solo vigente por email =====

    [Fact]
    public async Task AskingAgain_InvalidatesThePrevious_AndOnlyTheLastOneIsLive()
    {
        var email = UniqueEmail();
        var time = new FakeTimeProvider(Start);
        var first = await IssueAsync(email, time);
        time.Advance(TimeSpan.FromMinutes(5));

        var second = await IssueAsync(email, time);

        Assert.Equal(PendingRegistrationIssueOutcome.Issued, second.Outcome);
        Assert.NotEqual(first.Token, second.Token);
        var rows = await RowsOfAsync(email);
        Assert.Equal(2, rows.Count);
        Assert.Equal(Start.AddMinutes(5), rows[0].UsedAt); // la primera quedó anulada en ese instante
        Assert.Null(rows[1].UsedAt);
        Assert.False(rows[0].IsUsableAt(Start.AddMinutes(6)));
        Assert.True(rows[1].IsUsableAt(Start.AddMinutes(6)));
    }

    [Fact]
    public async Task AskingAgainAfterTheFirstExpiredWithoutBeingUsed_Works_TheTrapOfTheFilteredIndex()
    {
        // Una fila caducada sin canjear sigue teniendo UsedAt nulo: si solo se anularan las VIGENTES, esta chocaría con el índice único filtrado al insertar la nueva y el email
        // quedaría bloqueado para siempre. Es el caso que justifica anular TODAS las que no se hayan canjeado.
        var email = UniqueEmail();
        var time = new FakeTimeProvider(Start);
        await IssueAsync(email, time);
        time.Advance(TimeSpan.FromDays(3)); // caducó hace dos días

        var again = await IssueAsync(email, time);

        Assert.Equal(PendingRegistrationIssueOutcome.Issued, again.Outcome);
        var rows = await RowsOfAsync(email);
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.UsedAt is null);
    }

    [Fact]
    public async Task TwoPeopleAskingForDifferentEmails_DoNotInterfere()
    {
        var a = UniqueEmail("a");
        var b = UniqueEmail("b");
        var time = new FakeTimeProvider(Start);

        await IssueAsync(a, time);
        await IssueAsync(b, time);
        await IssueAsync(a, time);

        Assert.Single(await RowsOfAsync(b), r => r.UsedAt is null);
        Assert.Equal(2, (await RowsOfAsync(a)).Count);
    }

    // ===== El límite por email y hora =====

    [Fact]
    public async Task TheFourthRequestInAnHour_IsThrottled_AndCreatesNothing()
    {
        var email = UniqueEmail("limite");
        var time = new FakeTimeProvider(Start);
        for (var i = 0; i < PendingRegistration.MaxPerEmailPerHour; i++)
        {
            Assert.Equal(PendingRegistrationIssueOutcome.Issued, (await IssueAsync(email, time)).Outcome);
            time.Advance(TimeSpan.FromMinutes(1));
        }

        var fourth = await IssueAsync(email, time);

        Assert.Equal(PendingRegistrationIssueOutcome.Throttled, fourth.Outcome);
        Assert.Null(fourth.Token);
        var rows = await RowsOfAsync(email);
        Assert.Equal(3, rows.Count);
        Assert.Single(rows, r => r.UsedAt is null); // el tercero sigue vigente: limitar no anula nada
    }

    [Fact]
    public async Task TheLimitIsPerEmail_AnotherEmailIsNotAffected()
    {
        var limited = UniqueEmail("a");
        var time = new FakeTimeProvider(Start);
        for (var i = 0; i < PendingRegistration.MaxPerEmailPerHour + 1; i++)
        {
            await IssueAsync(limited, time);
        }

        var other = await IssueAsync(UniqueEmail("b"), time);

        Assert.Equal(PendingRegistrationIssueOutcome.Issued, other.Outcome);
    }

    [Fact]
    public async Task TheLimit_IsLiftedExactlyOneHourAfterTheCreationOfThoseThatCount()
    {
        // El borde exacto de la hora: se cuentan los creados DESPUÉS de «ahora - 1 h»; uno creado justo hace una hora ya no cuenta.
        var email = UniqueEmail("borde");
        var time = new FakeTimeProvider(Start);
        for (var i = 0; i < PendingRegistration.MaxPerEmailPerHour; i++)
        {
            await IssueAsync(email, time);
        }

        time.Advance(TimeSpan.FromHours(1).Subtract(TimeSpan.FromTicks(1)));
        Assert.Equal(PendingRegistrationIssueOutcome.Throttled, (await IssueAsync(email, time)).Outcome); // un tick antes: todavía cuentan

        time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(PendingRegistrationIssueOutcome.Issued, (await IssueAsync(email, time)).Outcome); // a la hora en punto: ya no cuentan
    }

    [Fact]
    public async Task AnExistingAccount_IsAnsweredBeforeTheLimitIsCounted()
    {
        // Con cuenta, ni se cuenta ni se crea: la respuesta es siempre «ya tiene cuenta», sin importar cuántas veces se pidió.
        var email = UniqueEmail("cuenta");
        await AccountWithEmailAsync(email);
        var time = new FakeTimeProvider(Start);

        for (var i = 0; i < PendingRegistration.MaxPerEmailPerHour + 2; i++)
        {
            Assert.Equal(PendingRegistrationIssueOutcome.AccountExists, (await IssueAsync(email, time)).Outcome);
        }

        Assert.Empty(await RowsOfAsync(email));
    }

    // ===== Carreras =====

    [Fact]
    public async Task TwoRequestsForTheSameEmailAtOnce_NeverLeaveTwoLiveRegistrations()
    {
        // El índice único filtrado lo garantiza en la base de datos y no solo en el código: la segunda que intenta insertar su vigente choca y se trata como «ahora no».
        for (var round = 0; round < 6; round++)
        {
            var email = UniqueEmail("carrera");
            var time = new FakeTimeProvider(Start);

            var results = await Task.WhenAll(IssueAsync(email, time), IssueAsync(email, time));

            Assert.Contains(results, r => r.Outcome == PendingRegistrationIssueOutcome.Issued);
            Assert.DoesNotContain(results, r => r.Outcome == PendingRegistrationIssueOutcome.AccountExists);
            var live = (await RowsOfAsync(email)).Where(r => r.UsedAt is null).ToList();
            Assert.Single(live);
        }
    }
}
