using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Security;
using OfiFlow.Application.Platform;
using OfiFlow.Application.Platform.Commands.VerifyPlatformChallenge;
using OfiFlow.Application.Tests.Common;

namespace OfiFlow.Application.Tests.Platform;

/// <summary>
/// El canje del desafío de plataforma (spec 011 R5, R8, R9, R10 y R17), con dobles: es el único sitio que emite la sesión, solo
/// canjea desafíos de plataforma, vuelve a comprobar que sigue siendo un operador activo y no devuelve el token si no se puede
/// escribir la auditoría. Que un desafío de empresa no cruce de mundo lo prueba contra SQL Server <c>MfaServiceTests</c>.
/// </summary>
public class VerifyPlatformChallengeCommandHandlerTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

    private readonly TestDbContext _db = TestDbContextFactory.Create();
    private readonly FakeMfaService _mfa = new();
    private readonly FakePlatformOperators _operators = new();
    private readonly FakePlatformTokenService _tokens = new();
    private readonly FakePlatformAudit _audit = new();
    private readonly ListLogger<VerifyPlatformChallengeCommandHandler> _logger = new();
    private readonly Guid _userId = Guid.NewGuid();

    // Un desafío y un código con la forma correcta (lo único que mira el handler antes de canjear).
    private readonly string _challenge = OpaqueToken.Generate().Value;
    private const string Code = "123456";

    public VerifyPlatformChallengeCommandHandlerTests()
    {
        _operators.MakeActive(_userId);
        _mfa.NextRedemption = new ChallengeRedemption(true, _userId);
    }

    private VerifyPlatformChallengeCommandHandler Handler() =>
        new(_db, _mfa, _operators, _tokens, _audit, new FakeTimeProvider(Now), _logger);

    private void AssertNoSessionWasIssued()
    {
        Assert.Empty(_tokens.Issued);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _db.SaveCount);
        Assert.DoesNotContain(_logger.Entries, entry => entry.EventId.Id == SecurityEventIds.PlatformSessionIssued);
    }

    [Fact]
    public async Task AValidChallengeAndCode_IssueTheSession_AuditItAndCommitOnce()
    {
        var session = await Handler().Handle(new VerifyPlatformChallengeCommand(_challenge, Code), CancellationToken.None);

        Assert.NotNull(session);
        Assert.Equal(_userId, Assert.Single(_tokens.Issued));
        Assert.Contains(_userId.ToString("N"), session.AccessToken);

        // R17: la fila de auditoría de la sesión emitida, confirmada antes de devolver el token.
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal((_userId, PlatformAuditActions.SessionIssued, (Guid?)null, Now), (entry.OperatorUserId, entry.Action, entry.TenantId, entry.At));
        Assert.Equal(1, _db.SaveCount);

        var logged = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.PlatformSessionIssued);
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.Contains(_userId.ToString(), logged.Message);
    }

    [Fact]
    public async Task TheChallengeIsRedeemedOnlyInThePlatformWorld_WithTheHashNotTheValue()
    {
        await Handler().Handle(new VerifyPlatformChallengeCommand(_challenge, Code), CancellationToken.None);

        // R9: se canjea con la audiencia de plataforma; un desafío de empresa se trata como uno inventado.
        Assert.Equal(ChallengeAudience.Platform, Assert.Single(_mfa.RedeemedAudiences));
        var redemption = Assert.Single(_mfa.Redemptions);
        Assert.Equal(OpaqueToken.Hash(_challenge), redemption.Hash);
        Assert.NotEqual(_challenge, redemption.Hash);
        Assert.Equal(Code, redemption.Code);
    }

    [Theory]
    [InlineData("", "123456")]
    [InlineData("no-es-un-desafio", "123456")]
    [InlineData("VALID", "12345")] // cinco cifras
    [InlineData("VALID", "abcdef")]
    public async Task AMalformedInput_IsRejectedBeforeAnythingIsLookedUp(string challenge, string code)
    {
        var result = await Handler().Handle(
            new VerifyPlatformChallengeCommand(challenge == "VALID" ? _challenge : challenge, code), CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(_mfa.Redemptions); // ni se hashea ni se busca nada (ADR-009 R3)
        AssertNoSessionWasIssued();
    }

    [Fact]
    public async Task ARejectedRedemption_GivesNoSession()
    {
        // Desafío desconocido, caducado, agotado, de empresa o código erróneo: el servicio del segundo factor responde «no».
        _mfa.NextRedemption = ChallengeRedemption.Rejected;

        var result = await Handler().Handle(new VerifyPlatformChallengeCommand(_challenge, Code), CancellationToken.None);

        Assert.Null(result);
        AssertNoSessionWasIssued();
    }

    [Fact]
    public async Task AnOperatorDisabledBetweenThePasswordAndTheCode_GetsNoSession()
    {
        // R10: el desafío y el código eran buenos, pero entre medias se dio de baja a la cuenta.
        await _operators.DisableAsync(_userId, Now, CancellationToken.None);

        var result = await Handler().Handle(new VerifyPlatformChallengeCommand(_challenge, Code), CancellationToken.None);

        Assert.Null(result);
        AssertNoSessionWasIssued();
        Assert.Contains(_logger.Entries, entry => entry.EventId.Id == SecurityEventIds.PlatformLoginRejected && entry.Message.Contains("NotAnActiveOperator"));
    }

    [Fact]
    public async Task WhenTheAuditCannotBeWritten_TheTokenNeverLeaves()
    {
        // R17: si la fila de auditoría no se puede confirmar, la excepción sube y el cliente no recibe sesión.
        _db.ExceptionOnSave = new DbUpdateException("la auditoría no se puede escribir");

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            Handler().Handle(new VerifyPlatformChallengeCommand(_challenge, Code), CancellationToken.None));

        Assert.DoesNotContain(_logger.Entries, entry => entry.EventId.Id == SecurityEventIds.PlatformSessionIssued);
    }

    [Fact]
    public async Task TheLog_NeverContainsTheChallengeOrTheCode()
    {
        await Handler().Handle(new VerifyPlatformChallengeCommand(_challenge, Code), CancellationToken.None);
        _mfa.NextRedemption = ChallengeRedemption.Rejected;
        await Handler().Handle(new VerifyPlatformChallengeCommand("no-es-un-desafio", "abcdef"), CancellationToken.None);

        var everything = string.Join("\n", _logger.Entries.Select(entry => entry.Message));
        Assert.NotEmpty(everything);
        Assert.DoesNotContain(_challenge, everything);
        Assert.DoesNotContain(Code, everything);
        Assert.DoesNotContain("no-es-un-desafio", everything);
    }
}
