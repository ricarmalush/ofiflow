using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Security;
using OfiFlow.Domain.Identity;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// Canjear un registro pendiente (spec 021, bloque 1) contra SQL Server real. El servicio marca el canje pero NO confirma: lo hace el caso de uso en su única <c>SaveChangesAsync</c>. Por
/// eso cada test confirma a mano, y la carrera de dos canjes a la vez se prueba con dos contextos de verdad: la decide la versión de fila de la base de datos, no el código.
/// </summary>
public class PendingRegistrationConsumeTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private static readonly DateTime Start = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerContainerFixture _fixture = fixture;

    private ApplicationDbContext Open() => _fixture.CreateDbContext(Guid.NewGuid());

    private static string UniqueEmail(string prefix = "persona") => $"{prefix}-{Guid.NewGuid():N}@example.com";

    private async Task<string> IssueAsync(string email, FakeTimeProvider time, string company = "Fontanería Demo SL", string person = "Ana Pérez", string terms = "2026-11")
    {
        await using var db = Open();
        var issue = await new PendingRegistrationService(db, time).IssueAsync(email, company, person, terms, CancellationToken.None);
        Assert.Equal(PendingRegistrationIssueOutcome.Issued, issue.Outcome);
        return issue.Token!;
    }

    /// <summary>Canjea y confirma, como hará el caso de uso (que además crea la empresa y el usuario en esa misma confirmación).</summary>
    private async Task<PendingRegistrationConsumption> ConsumeAndCommitAsync(string token, FakeTimeProvider time)
    {
        await using var db = Open();
        var result = await new PendingRegistrationService(db, time).ConsumeAsync(token, CancellationToken.None);
        await db.SaveChangesAsync();
        return result;
    }

    private async Task<List<PendingRegistration>> RowsOfAsync(string email)
    {
        var normalized = email.Trim().ToUpperInvariant();
        await using var db = Open();
        return await db.PendingRegistrations.AsNoTracking().Where(r => r.NormalizedEmail == normalized).OrderBy(r => r.CreatedAt).ToListAsync();
    }

    // ===== Un canje bueno =====

    [Fact]
    public async Task AValidToken_IsConsumed_AndReturnsWhatWasWrittenWhenAskingForIt()
    {
        var email = UniqueEmail();
        var time = new FakeTimeProvider(Start);
        var token = await IssueAsync(email, time, company: "Electricidad Gil SL", person: "Luis Gil", terms: "2026-11");
        time.Advance(TimeSpan.FromMinutes(10));

        var result = await ConsumeAndCommitAsync(token, time);

        Assert.Equal(PendingRegistrationConsumeOutcome.Consumed, result.Outcome);
        Assert.Equal(email, result.Email);
        Assert.Equal("Electricidad Gil SL", result.CompanyName);
        Assert.Equal("Luis Gil", result.UserName);
        Assert.Equal("2026-11", result.TermsVersion);
        Assert.Equal(Start, result.TermsAcceptedAt); // la fecha de ACEPTAR los términos es la de pedirlo, no la de abrir el enlace
        Assert.Null(result.RejectedReason);
    }

    [Fact]
    public async Task Consuming_MarksTheRowUsed_AtTheMomentOfTheConsumption()
    {
        var email = UniqueEmail();
        var time = new FakeTimeProvider(Start);
        var token = await IssueAsync(email, time);
        time.Advance(TimeSpan.FromHours(2));

        await ConsumeAndCommitAsync(token, time);

        var row = Assert.Single(await RowsOfAsync(email));
        Assert.Equal(Start.AddHours(2), row.UsedAt);
    }

    [Fact]
    public async Task ATokenIsValidUntilTheLastInstant_AndNotAtTheExpiry()
    {
        var email = UniqueEmail();
        var time = new FakeTimeProvider(Start);
        var token = await IssueAsync(email, time);

        time.Advance(PendingRegistration.Lifetime.Subtract(TimeSpan.FromTicks(1)));
        var last = await ConsumeAndCommitAsync(token, time);

        Assert.Equal(PendingRegistrationConsumeOutcome.Consumed, last.Outcome);

        var otherEmail = UniqueEmail();
        var otherTime = new FakeTimeProvider(Start);
        var otherToken = await IssueAsync(otherEmail, otherTime);
        otherTime.Advance(PendingRegistration.Lifetime);
        var exact = await ConsumeAndCommitAsync(otherToken, otherTime);

        Assert.Equal(PendingRegistrationConsumeOutcome.Rejected, exact.Outcome);
        Assert.Equal("Expired", exact.RejectedReason);
    }

    // ===== Rechazos: todos distintos por dentro, ninguno por fuera =====

    [Fact]
    public async Task AnUnknownToken_IsRejected_WithReasonUnknown_AndTouchesNothing()
    {
        var email = UniqueEmail();
        var time = new FakeTimeProvider(Start);
        await IssueAsync(email, time);
        var (strangeToken, _) = OpaqueToken.Generate(); // bien formado, pero nadie lo pidió

        var result = await ConsumeAndCommitAsync(strangeToken, time);

        Assert.Equal(PendingRegistrationConsumeOutcome.Rejected, result.Outcome);
        Assert.Equal("Unknown", result.RejectedReason);
        Assert.Null(result.Email);
        Assert.Null(Assert.Single(await RowsOfAsync(email)).UsedAt); // el que sí existe sigue vigente
    }

    [Fact]
    public async Task ATokenUsedBefore_IsRejected_WithReasonUsedOrReplaced()
    {
        var email = UniqueEmail();
        var time = new FakeTimeProvider(Start);
        var token = await IssueAsync(email, time);
        await ConsumeAndCommitAsync(token, time);

        var again = await ConsumeAndCommitAsync(token, time);

        Assert.Equal(PendingRegistrationConsumeOutcome.Rejected, again.Outcome);
        Assert.Equal("UsedOrReplaced", again.RejectedReason);
        Assert.Null(again.Email); // y no devuelve ningún dato de la persona
        Assert.Null(again.CompanyName);
    }

    [Fact]
    public async Task ATokenReplacedByANewRequest_IsRejected_AndTheNewOneWorks()
    {
        var email = UniqueEmail();
        var time = new FakeTimeProvider(Start);
        var oldToken = await IssueAsync(email, time);
        time.Advance(TimeSpan.FromMinutes(1));
        var newToken = await IssueAsync(email, time);

        var old = await ConsumeAndCommitAsync(oldToken, time);
        var current = await ConsumeAndCommitAsync(newToken, time);

        Assert.Equal(PendingRegistrationConsumeOutcome.Rejected, old.Outcome);
        Assert.Equal("UsedOrReplaced", old.RejectedReason);
        Assert.Equal(PendingRegistrationConsumeOutcome.Consumed, current.Outcome);
    }

    [Fact]
    public async Task AnExpiredToken_IsRejected_WithReasonExpired_AndIsNotMarkedUsed()
    {
        var email = UniqueEmail();
        var time = new FakeTimeProvider(Start);
        var token = await IssueAsync(email, time);
        time.Advance(TimeSpan.FromDays(2));

        var result = await ConsumeAndCommitAsync(token, time);

        Assert.Equal(PendingRegistrationConsumeOutcome.Rejected, result.Outcome);
        Assert.Equal("Expired", result.RejectedReason);
        Assert.Null(Assert.Single(await RowsOfAsync(email)).UsedAt); // rechazar no escribe nada
    }

    [Fact]
    public async Task AnEmailThatGotAnAccountAfterAskingForIt_IsRejected_WithReasonEmailTaken_AndNothingIsMarked()
    {
        // Alguien creó la cuenta por otra vía (una invitación, por ejemplo) entre pedir el registro y abrir el enlace.
        var email = UniqueEmail();
        var time = new FakeTimeProvider(Start);
        var token = await IssueAsync(email, time);
        await using (var db = Open())
        {
            db.ApplicationUsers.Add(new ApplicationUser(Guid.NewGuid(), email));
            await db.SaveChangesAsync();
        }

        var result = await ConsumeAndCommitAsync(token, time);

        Assert.Equal(PendingRegistrationConsumeOutcome.Rejected, result.Outcome);
        Assert.Equal("EmailTaken", result.RejectedReason);
        Assert.Null(Assert.Single(await RowsOfAsync(email)).UsedAt);
    }

    [Fact]
    public async Task TheTokenOfAnotherEmail_NeverConsumesThisOne()
    {
        var a = UniqueEmail("a");
        var b = UniqueEmail("b");
        var time = new FakeTimeProvider(Start);
        var tokenA = await IssueAsync(a, time);
        await IssueAsync(b, time);

        await ConsumeAndCommitAsync(tokenA, time);

        Assert.Null(Assert.Single(await RowsOfAsync(b)).UsedAt);
    }

    [Fact]
    public async Task TheTokenIsLookedUpByItsHash_TheHashItselfIsNotAValidToken()
    {
        var email = UniqueEmail();
        var time = new FakeTimeProvider(Start);
        var token = await IssueAsync(email, time);
        var storedHash = Assert.Single(await RowsOfAsync(email)).TokenHash;

        // Quien leyera la base de datos solo vería el hash: presentarlo como token no debe servir (se vuelve a hashear y no coincide con nada).
        var result = await ConsumeAndCommitAsync(storedHash, time);

        Assert.Equal(PendingRegistrationConsumeOutcome.Rejected, result.Outcome);
        Assert.Equal("Unknown", result.RejectedReason);
        Assert.NotEqual(token, storedHash);
    }

    // ===== Carreras =====

    [Fact]
    public async Task TwoConsumptionsAtOnce_OnlyOneCommits_TheOtherIsStoppedByTheRowVersion()
    {
        // Los dos leen «sin usar» y marcan el canje; el primero en confirmar gana y el UPDATE del segundo ya no encuentra la fila como la leyó. Es la garantía de que de un enlace sale UNA cuenta.
        for (var round = 0; round < 4; round++)
        {
            var email = UniqueEmail("carrera");
            var time = new FakeTimeProvider(Start);
            var token = await IssueAsync(email, time);

            await using var first = Open();
            await using var second = Open();
            var firstResult = await new PendingRegistrationService(first, time).ConsumeAsync(token, CancellationToken.None);
            var secondResult = await new PendingRegistrationService(second, time).ConsumeAsync(token, CancellationToken.None);
            Assert.Equal(PendingRegistrationConsumeOutcome.Consumed, firstResult.Outcome);
            Assert.Equal(PendingRegistrationConsumeOutcome.Consumed, secondResult.Outcome); // los dos creen que pueden: aún nadie ha confirmado

            await first.SaveChangesAsync();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

            Assert.NotNull(Assert.Single(await RowsOfAsync(email)).UsedAt);
        }
    }

    [Fact]
    public async Task ConsumingWhileAnotherRequestReplacesIt_NeverLeavesTwoLive()
    {
        var email = UniqueEmail("pisa");
        var time = new FakeTimeProvider(Start);
        var token = await IssueAsync(email, time);

        await using var consumer = Open();
        var consumption = await new PendingRegistrationService(consumer, time).ConsumeAsync(token, CancellationToken.None);
        Assert.Equal(PendingRegistrationConsumeOutcome.Consumed, consumption.Outcome);

        // Mientras tanto, otra persona vuelve a pedirlo: anula el que está a medio canjear.
        time.Advance(TimeSpan.FromSeconds(1));
        await IssueAsync(email, time);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => consumer.SaveChangesAsync());

        Assert.Single(await RowsOfAsync(email), r => r.UsedAt is null); // solo el nuevo
    }
}
