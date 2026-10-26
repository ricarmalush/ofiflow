using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Platform.Commands.PlatformLogin;
using OfiFlow.Application.Tests.Common;

namespace OfiFlow.Application.Tests.Platform;

/// <summary>
/// El primer paso del inicio de sesión de plataforma (spec 011 R5 y R6), con dobles: siempre un desafío o un único «no», nunca una
/// sesión, y todos los fallos iguales para quien llama. La verificación real de la contraseña (que cuesta lo mismo en todos los
/// casos) se comprueba contando llamadas en el extremo a extremo contra SQL Server.
/// </summary>
public class PlatformLoginCommandHandlerTests
{
    private const string Email = "operadora@ofiflow.example";
    private const string Password = "una-clave-larga-123";

    private static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

    private readonly TestDbContext _db = TestDbContextFactory.Create();
    private readonly FakeIdentityService _identity = new();
    private readonly FakePlatformOperators _operators = new();
    private readonly FakeMfaService _mfa = new();
    private readonly ListLogger<PlatformLoginCommandHandler> _logger = new();

    private PlatformLoginCommandHandler Handler() => new(_db, _identity, _operators, _mfa, new FakeTimeProvider(Now), _logger);

    private async Task<Guid> OperatorAsync(bool withSecondFactor = true)
    {
        var id = Guid.NewGuid();
        await _identity.CreateUserAsync(id, Email, Password, CancellationToken.None);
        _operators.MakeActive(id);

        if (withSecondFactor)
        {
            _identity.AccountsWithSecondFactor.Add(id);
        }

        return id;
    }

    private void AssertNoChallengeWasCreated()
    {
        Assert.Empty(_mfa.CreatedChallenges);
        Assert.Equal(0, _db.SaveCount);
    }

    private (LogLevel Level, EventId EventId, string Message) TheRejection() =>
        Assert.Single(_logger.Entries, entry => entry.EventId.Id == SecurityEventIds.PlatformLoginRejected);

    [Fact]
    public async Task AnActiveOperatorWithTheRightPassword_GetsAChallenge_ForThePlatformWorld()
    {
        var userId = await OperatorAsync();

        var challenge = await Handler().Handle(new PlatformLoginCommand(Email, Password), CancellationToken.None);

        Assert.NotNull(challenge);
        Assert.Equal(Now + AuthenticationPolicy.ChallengeLifetime, challenge.ExpiresAt);

        // R9: el desafío nace para la PLATAFORMA: solo se canjea ahí, nunca por una sesión de empresa.
        var created = Assert.Single(_mfa.CreatedChallenges);
        Assert.Equal(userId, created.UserId);
        Assert.Equal(ChallengeAudience.Platform, Assert.Single(_mfa.ChallengeAudiences));
        Assert.Equal(1, _db.SaveCount);
        Assert.Empty(_logger.Entries); // un acierto no deja evento de rechazo
    }

    [Fact]
    public async Task TheChallengeIsStoredOnlyAsAHash_NeverTheValueTheClientReceives()
    {
        await OperatorAsync();

        var challenge = await Handler().Handle(new PlatformLoginCommand(Email, Password), CancellationToken.None);

        var created = Assert.Single(_mfa.CreatedChallenges);
        Assert.NotEqual(challenge!.Challenge, created.Hash);
    }

    [Fact]
    public async Task AWrongPassword_IsRejected_WithNoChallenge()
    {
        await OperatorAsync();

        var result = await Handler().Handle(new PlatformLoginCommand(Email, "otra-clave-larga-123"), CancellationToken.None);

        Assert.Null(result);
        AssertNoChallengeWasCreated();
        Assert.Equal("InvalidCredentials", ReasonOf(TheRejection()));
    }

    [Fact]
    public async Task AnUnknownEmail_IsRejected_WithNoChallenge()
    {
        var result = await Handler().Handle(new PlatformLoginCommand("nadie@ofiflow.example", Password), CancellationToken.None);

        Assert.Null(result);
        AssertNoChallengeWasCreated();
        Assert.Equal("InvalidCredentials", ReasonOf(TheRejection()));
    }

    [Fact]
    public async Task ACompanyAccountWithItsCorrectPassword_LearnsNothing()
    {
        // R6: una cuenta de EMPRESA con su contraseña buena recibe lo mismo que quien se equivoca: null (401). El motivo real
        // (no es un operador) solo va al registro, con su UserId.
        var companyUserId = Guid.NewGuid();
        await _identity.CreateUserAsync(companyUserId, Email, Password, CancellationToken.None);
        _identity.AccountsWithSecondFactor.Add(companyUserId); // incluso con segundo factor propio

        var result = await Handler().Handle(new PlatformLoginCommand(Email, Password), CancellationToken.None);

        Assert.Null(result);
        AssertNoChallengeWasCreated();
        var rejection = TheRejection();
        Assert.Equal("NotAnActiveOperator", ReasonOf(rejection));
        Assert.Contains(companyUserId.ToString(), rejection.Message);
    }

    [Fact]
    public async Task ADisabledOperator_IsRejected_WithNoChallenge()
    {
        var userId = await OperatorAsync();
        await _operators.DisableAsync(userId, Now, CancellationToken.None);

        var result = await Handler().Handle(new PlatformLoginCommand(Email, Password), CancellationToken.None);

        Assert.Null(result);
        AssertNoChallengeWasCreated();
        Assert.Equal("NotAnActiveOperator", ReasonOf(TheRejection()));
    }

    [Fact]
    public async Task AnOperatorWithoutASecondFactor_NeverGetsAnythingFromThePasswordAlone()
    {
        // R5 y D4: no hay operador sin segundo factor. El alta lo exige, así que esto no debería ocurrir; si ocurriera, la
        // contraseña sola no da ni un desafío (y mucho menos una sesión).
        await OperatorAsync(withSecondFactor: false);

        var result = await Handler().Handle(new PlatformLoginCommand(Email, Password), CancellationToken.None);

        Assert.Null(result);
        AssertNoChallengeWasCreated();
        Assert.Equal("NoSecondFactor", ReasonOf(TheRejection()));
    }

    [Fact]
    public async Task TheLog_NeverContainsTheEmailOrThePassword()
    {
        await OperatorAsync();

        await Handler().Handle(new PlatformLoginCommand(Email, "otra-clave-larga-123"), CancellationToken.None);
        await Handler().Handle(new PlatformLoginCommand(Email, Password), CancellationToken.None);

        var everything = string.Join("\n", _logger.Entries.Select(entry => entry.Message));
        Assert.NotEmpty(everything); // control positivo: sí se registró algo
        Assert.DoesNotContain(Email, everything);
        Assert.DoesNotContain("operadora", everything);
        Assert.DoesNotContain(Password, everything);
        Assert.DoesNotContain("otra-clave-larga-123", everything);
    }

    private static string ReasonOf((LogLevel Level, EventId EventId, string Message) entry)
    {
        Assert.Equal(LogLevel.Warning, entry.Level);

        return new[] { "InvalidCredentials", "NotAnActiveOperator", "NoSecondFactor" }.Single(reason => entry.Message.Contains(reason, StringComparison.Ordinal));
    }
}
