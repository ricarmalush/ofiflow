using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Security;
using OfiFlow.Domain.Identity;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// Pedir un token de restablecimiento (spec 018, bloque 2) contra SQL Server real: crear, anular el anterior, limitar por cuenta y hora, y tratar a un operador como «no existe».
/// Lo que se prueba es lo que decide si el flujo se puede abusar: cada caso de «se crea» va con su control de «no se crea».
/// </summary>
public class PasswordResetIssueTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private static readonly DateTime Start = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerContainerFixture _fixture = fixture;

    private ApplicationDbContext Open() => _fixture.CreateDbContext(Guid.NewGuid());

    private async Task<Guid> AccountAsync(string email)
    {
        var id = Guid.NewGuid();
        await using var db = Open();
        db.ApplicationUsers.Add(new ApplicationUser(id, email));
        await db.SaveChangesAsync();
        return id;
    }

    private async Task MakeOperatorAsync(Guid userId, bool disabled = false)
    {
        await using var db = Open();
        var op = PlatformOperator.Create(userId, Start);
        if (disabled)
        {
            op.Disable(Start.AddMinutes(1));
        }

        db.PlatformOperators.Add(op);
        await db.SaveChangesAsync();
    }

    private async Task<PasswordResetIssue> IssueAsync(string email, FakeTimeProvider time)
    {
        await using var db = Open();
        return await new PasswordResetService(db, time).IssueAsync(email, CancellationToken.None);
    }

    private async Task<List<PasswordResetToken>> RowsOfAsync(Guid userId)
    {
        await using var db = Open();
        return await db.PasswordResetTokens.AsNoTracking().Where(t => t.UserId == userId).OrderBy(t => t.CreatedAt).ToListAsync();
    }

    private static string UniqueEmail(string prefix = "persona") => $"{prefix}-{Guid.NewGuid():N}@example.com";

    // ===== Sin cuenta =====

    [Fact]
    public async Task AnUnknownEmail_IsUnknownAccount_AndCreatesNothing()
    {
        var before = await CountAllAsync();

        var issue = await IssueAsync(UniqueEmail("no-existe"), new FakeTimeProvider(Start));

        Assert.Equal(PasswordResetIssueOutcome.UnknownAccount, issue.Outcome);
        Assert.Null(issue.UserId);
        Assert.Null(issue.Token);
        Assert.Equal(before, await CountAllAsync());
    }

    private async Task<int> CountAllAsync()
    {
        await using var db = Open();
        return await db.PasswordResetTokens.CountAsync();
    }

    // ===== Con cuenta: se crea un token y solo su hash queda =====

    [Fact]
    public async Task AnExistingAccount_GetsAToken_WhoseHashIsStored_AndTheValueIsReturnedOnce()
    {
        var email = UniqueEmail();
        var userId = await AccountAsync(email);

        var issue = await IssueAsync(email, new FakeTimeProvider(Start));

        Assert.Equal(PasswordResetIssueOutcome.Issued, issue.Outcome);
        Assert.Equal(userId, issue.UserId);
        Assert.NotNull(issue.Token);
        Assert.True(OpaqueToken.IsWellFormed(issue.Token));

        var row = Assert.Single(await RowsOfAsync(userId));
        Assert.Equal(OpaqueToken.Hash(issue.Token), row.TokenHash);
        Assert.NotEqual(issue.Token, row.TokenHash);
        Assert.Equal(Start, row.CreatedAt);
        Assert.Equal(Start.AddHours(1), row.ExpiresAt);
        Assert.Null(row.UsedAt);
        Assert.True(row.IsUsableAt(Start.AddMinutes(30)));
    }

    [Fact]
    public async Task TheTokenInClear_IsInNoColumnOfTheRow()
    {
        var email = UniqueEmail();
        var userId = await AccountAsync(email);

        var issue = await IssueAsync(email, new FakeTimeProvider(Start));

        var row = Assert.Single(await RowsOfAsync(userId));
        var everything = string.Join("|", row.Id, row.UserId, row.TokenHash, row.CreatedAt, row.ExpiresAt, row.UsedAt);
        Assert.DoesNotContain(issue.Token!, everything);
        Assert.DoesNotContain(email, everything);
    }

    [Theory]
    [InlineData("upper")]
    [InlineData("mixed")]
    [InlineData("same")]
    public async Task TheEmailIsMatchedWithoutCaseSensitivity_LikeTheLoginDoes(string variant)
    {
        var stored = UniqueEmail("ana.mayusculas");
        var userId = await AccountAsync(stored);
        var typed = variant switch
        {
            "upper" => stored.ToUpperInvariant(),
            "mixed" => string.Concat(stored.Select((c, i) => i % 2 == 0 ? char.ToUpperInvariant(c) : c)),
            _ => stored
        };

        var issue = await IssueAsync(typed, new FakeTimeProvider(Start));

        Assert.Equal(PasswordResetIssueOutcome.Issued, issue.Outcome);
        Assert.Equal(userId, issue.UserId);
    }

    [Fact]
    public async Task TwoTokensOfDifferentAccounts_NeverCollide_AndEachBelongsToItsOwnAccount()
    {
        var a = UniqueEmail();
        var b = UniqueEmail();
        var userA = await AccountAsync(a);
        var userB = await AccountAsync(b);
        var time = new FakeTimeProvider(Start);

        var issueA = await IssueAsync(a, time);
        var issueB = await IssueAsync(b, time);

        Assert.NotEqual(issueA.Token, issueB.Token);
        Assert.Equal(OpaqueToken.Hash(issueA.Token!), Assert.Single(await RowsOfAsync(userA)).TokenHash);
        Assert.Equal(OpaqueToken.Hash(issueB.Token!), Assert.Single(await RowsOfAsync(userB)).TokenHash);
    }

    // ===== Un solo token vigente por cuenta (R3) =====

    [Fact]
    public async Task AskingAgain_InvalidatesThePreviousToken_SoOnlyTheLastOneIsUsable()
    {
        var email = UniqueEmail();
        var userId = await AccountAsync(email);
        var time = new FakeTimeProvider(Start);

        var first = await IssueAsync(email, time);
        time.Advance(TimeSpan.FromMinutes(5));
        var second = await IssueAsync(email, time);

        var rows = await RowsOfAsync(userId);
        Assert.Equal(2, rows.Count);
        var firstRow = rows.Single(r => r.TokenHash == OpaqueToken.Hash(first.Token!));
        var secondRow = rows.Single(r => r.TokenHash == OpaqueToken.Hash(second.Token!));
        Assert.False(firstRow.IsUsableAt(time.GetUtcNow().UtcDateTime));   // el del correo viejo ya no vale
        Assert.True(secondRow.IsUsableAt(time.GetUtcNow().UtcDateTime));
        Assert.Equal(1, rows.Count(r => r.IsUsableAt(time.GetUtcNow().UtcDateTime)));
    }

    [Fact]
    public async Task InvalidatingThePrevious_DoesNotTouchTheTokensOfOtherAccounts()
    {
        var a = UniqueEmail();
        var b = UniqueEmail();
        var userA = await AccountAsync(a);
        var userB = await AccountAsync(b);
        var time = new FakeTimeProvider(Start);
        await IssueAsync(b, time);

        await IssueAsync(a, time);
        await IssueAsync(a, time);

        Assert.True(Assert.Single(await RowsOfAsync(userB)).IsUsableAt(Start.AddMinutes(1)));
        Assert.Equal(2, (await RowsOfAsync(userA)).Count);
    }

    // ===== El límite por cuenta y hora (R4) =====

    [Fact]
    public async Task TheFourthRequestOfTheHour_IsThrottled_CreatesNothing_AndDoesNotInvalidateTheLastToken()
    {
        var email = UniqueEmail();
        var userId = await AccountAsync(email);
        var time = new FakeTimeProvider(Start);
        PasswordResetIssue last = null!;
        for (var i = 0; i < PasswordResetToken.MaxPerAccountPerHour; i++)
        {
            last = await IssueAsync(email, time);
            time.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(PasswordResetIssueOutcome.Issued, last.Outcome);
        }

        var fourth = await IssueAsync(email, time);

        Assert.Equal(PasswordResetIssueOutcome.Throttled, fourth.Outcome);
        Assert.Equal(userId, fourth.UserId);
        Assert.Null(fourth.Token);
        var rows = await RowsOfAsync(userId);
        Assert.Equal(3, rows.Count);
        // Pedir más no deja a la persona sin su enlace: el último token, el del tercer correo, sigue siendo utilizable.
        Assert.True(rows.Single(r => r.TokenHash == OpaqueToken.Hash(last.Token!)).IsUsableAt(time.GetUtcNow().UtcDateTime));
    }

    [Fact]
    public async Task TheLimitIsAnHourWindow_AnInstantBeforeItTheCountStands_AtTheExactEndItDoesNot()
    {
        var email = UniqueEmail();
        await AccountAsync(email);
        var time = new FakeTimeProvider(Start);
        for (var i = 0; i < 3; i++)
        {
            await IssueAsync(email, time);
        }

        time.Advance(TimeSpan.FromHours(1) - TimeSpan.FromTicks(1));
        Assert.Equal(PasswordResetIssueOutcome.Throttled, (await IssueAsync(email, time)).Outcome); // un instante antes: aún cuentan

        time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(PasswordResetIssueOutcome.Issued, (await IssueAsync(email, time)).Outcome);   // en el instante exacto: ya no
    }

    [Fact]
    public async Task UsedTokensStillCountTowardsTheLimit_BecauseItIsTheEmailsThatArriveWhatIsLimited()
    {
        var email = UniqueEmail();
        var userId = await AccountAsync(email);
        var time = new FakeTimeProvider(Start);
        for (var i = 0; i < 3; i++)
        {
            await IssueAsync(email, time);
        }

        await using (var db = Open())
        {
            foreach (var token in await db.PasswordResetTokens.Where(t => t.UserId == userId).ToListAsync())
            {
                token.Invalidate(Start.AddMinutes(1));
            }

            await db.SaveChangesAsync();
        }

        Assert.Equal(PasswordResetIssueOutcome.Throttled, (await IssueAsync(email, time)).Outcome);
    }

    [Fact]
    public async Task TheLimitOfOneAccount_DoesNotAffectAnother()
    {
        var a = UniqueEmail();
        var b = UniqueEmail();
        await AccountAsync(a);
        await AccountAsync(b);
        var time = new FakeTimeProvider(Start);
        for (var i = 0; i < 3; i++)
        {
            await IssueAsync(a, time);
        }

        Assert.Equal(PasswordResetIssueOutcome.Throttled, (await IssueAsync(a, time)).Outcome);   // control: A sí está limitada
        Assert.Equal(PasswordResetIssueOutcome.Issued, (await IssueAsync(b, time)).Outcome);
    }

    // ===== Operadores de la plataforma (R14) =====

    [Fact]
    public async Task AnActiveOperator_IsTreatedAsNoAccount_AndGetsNoToken()
    {
        var email = UniqueEmail("operadora");
        var userId = await AccountAsync(email);
        await MakeOperatorAsync(userId);

        var issue = await IssueAsync(email, new FakeTimeProvider(Start));

        Assert.Equal(PasswordResetIssueOutcome.UnknownAccount, issue.Outcome);
        Assert.Null(issue.UserId);   // ni siquiera el Id: no debe distinguirse de «no existe» ni en el registro
        Assert.Empty(await RowsOfAsync(userId));
    }

    [Fact]
    public async Task ADisabledOperator_IsTreatedAsNoAccountEither()
    {
        var email = UniqueEmail("exoperadora");
        var userId = await AccountAsync(email);
        await MakeOperatorAsync(userId, disabled: true);

        var issue = await IssueAsync(email, new FakeTimeProvider(Start));

        Assert.Equal(PasswordResetIssueOutcome.UnknownAccount, issue.Outcome);
        Assert.Empty(await RowsOfAsync(userId));
    }

    // ===== Una carrera con un uso del token anterior =====

    private sealed class FailingSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new DbUpdateConcurrencyException("la versión de fila cambió");
    }

    [Fact]
    public async Task WhenSavingFailsByARace_ItIsTreatedAsNotNow_NotAsAnErrorOnlyAccountsThatExistWouldGet()
    {
        // Responder 500 aquí solo ocurriría con cuentas que existen: un oráculo raro pero real. Se responde como si se hubiera pasado el límite.
        var email = UniqueEmail();
        var userId = await AccountAsync(email);
        await using var db = _fixture.CreateDbContext(Guid.NewGuid(), options => options.AddInterceptors(new FailingSave()));

        var issue = await new PasswordResetService(db, new FakeTimeProvider(Start)).IssueAsync(email, CancellationToken.None);

        Assert.Equal(PasswordResetIssueOutcome.Throttled, issue.Outcome);
        Assert.Equal(userId, issue.UserId);
        Assert.Null(issue.Token);
        Assert.Empty(await RowsOfAsync(userId));
    }
}
