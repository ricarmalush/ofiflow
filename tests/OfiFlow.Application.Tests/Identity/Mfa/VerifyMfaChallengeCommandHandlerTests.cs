using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Security;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Commands.VerifyMfaChallenge;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Identity.Mfa;

/// <summary>
/// El segundo paso del login (spec 010 R11-R13): canjear el desafío por la sesión. El canje real (atómico, con los
/// intentos y el bloqueo) vive en <c>MfaService</c> y se prueba contra SQL Server; aquí, qué hace el handler con el resultado.
/// </summary>
public class VerifyMfaChallengeCommandHandlerTests : MfaTestBase
{
    private readonly ListLogger<VerifyMfaChallengeCommandHandler> _logger = new();

    private VerifyMfaChallengeCommandHandler Handler() => new(Db, Mfa, Tokens, _logger);

    private static string ValidChallenge() => OpaqueToken.Generate().Value;

    private Task<AuthResultDto?> VerifyAsync(string? challenge = null, string code = "123456") =>
        Handler().Handle(new VerifyMfaChallengeCommand(challenge ?? ValidChallenge(), code), CancellationToken.None);

    private async Task<Guid> SeedMembershipAsync(TenantRole role = TenantRole.Technician)
    {
        Db.TenantUsers.Add(TenantUser.Create(TenantId, UserId, role));
        await Db.SaveChangesAsync(CancellationToken.None);

        return TenantId;
    }

    // ===== El canje que sale bien =====

    [Fact]
    public async Task AGoodRedemption_IssuesTheSession_ForTheMembershipsCompanyAndRole()
    {
        await SeedMembershipAsync(TenantRole.Manager);
        Mfa.NextRedemption = new ChallengeRedemption(true, UserId);

        var result = await VerifyAsync();

        Assert.NotNull(result);
        Assert.Equal([(UserId, TenantId, TenantRole.Manager)], Tokens.Issued); // empresa y rol de la pertenencia
    }

    [Fact]
    public async Task TheChallengeIsRedeemedByItsHash_NeverByItsValue()
    {
        await SeedMembershipAsync();
        Mfa.NextRedemption = new ChallengeRedemption(true, UserId);
        var challenge = ValidChallenge();

        await VerifyAsync(challenge, "654321");

        var redemption = Assert.Single(Mfa.Redemptions);
        Assert.Equal(OpaqueToken.Hash(challenge), redemption.Hash);
        Assert.NotEqual(challenge, redemption.Hash);
        Assert.Equal("654321", redemption.Code);
    }

    [Fact]
    public async Task ARecoveryCode_IsPassedThroughToo()
    {
        await SeedMembershipAsync();
        Mfa.NextRedemption = new ChallengeRedemption(true, UserId);

        var result = await VerifyAsync(code: "ABCD-EFGH-JKMN-PQRS");

        Assert.NotNull(result);
        Assert.Equal("ABCD-EFGH-JKMN-PQRS", Assert.Single(Mfa.Redemptions).Code);
    }

    // ===== Todos los fallos son iguales: nada de sesión (R12, R13) =====

    [Fact]
    public async Task ARejectedRedemption_IssuesNoSession()
    {
        await SeedMembershipAsync();
        Mfa.NextRedemption = ChallengeRedemption.Rejected;

        Assert.Null(await VerifyAsync());
        Assert.Equal(0, Tokens.IssuedSessions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("corto")]
    [InlineData("esto-no-es-un-desafio-esto-no-es-un-desafio!")]
    public async Task AMalformedChallenge_IsRejected_WithoutEvenAskingTheService(string challenge)
    {
        await SeedMembershipAsync();
        Mfa.NextRedemption = new ChallengeRedemption(true, UserId); // aunque el servicio dijera que sí, ni se le pregunta

        Assert.Null(await VerifyAsync(challenge));

        Assert.Empty(Mfa.Redemptions);
        Assert.Equal(0, Tokens.IssuedSessions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("no es un código")]
    [InlineData("ABCD-EFGH-JKMN-PQRU")]
    public async Task AMalformedCode_IsRejected_WithoutEvenAskingTheService(string code)
    {
        await SeedMembershipAsync();
        Mfa.NextRedemption = new ChallengeRedemption(true, UserId);

        Assert.Null(await VerifyAsync(code: code));

        Assert.Empty(Mfa.Redemptions);
        Assert.Equal(0, Tokens.IssuedSessions);
    }

    [Fact]
    public async Task AMalformedInput_IsLoggedAsARejectedSecondStep_WithoutTheValues()
    {
        var challenge = "esto-no-es-un-desafio";

        await VerifyAsync(challenge, "no es un código");

        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.MfaVerificationFailed);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.DoesNotContain(challenge, entry.Message);
        Assert.DoesNotContain("no es un código", entry.Message);
    }

    [Fact]
    public async Task ACorrectSecondFactor_ForAnAccountWithoutMembership_IssuesNoSession()
    {
        // Sin pertenencia a ninguna empresa no hay a qué empresa emitir la sesión.
        Mfa.NextRedemption = new ChallengeRedemption(true, UserId);

        Assert.Null(await VerifyAsync());

        Assert.Equal(0, Tokens.IssuedSessions);
        Assert.Contains(_logger.Entries, e => e.EventId.Id == SecurityEventIds.LoginWithoutTenantMembership && e.Message.Contains(UserId.ToString()));
    }

    [Fact]
    public async Task TheSession_IsNeverForACompanyTheClientChooses()
    {
        // La petición solo trae el desafío y el código: no hay campo para empresa, rol ni cuenta.
        var properties = typeof(VerifyMfaChallengeCommand).GetProperties().Select(p => p.Name);

        Assert.Equal(["Challenge", "Code"], properties);
        await Task.CompletedTask;
    }

    // ===== Validador: solo un tope de tamaño =====

    [Theory]
    [InlineData("", "123456", false)]
    [InlineData("x", "", false)]
    [InlineData("x", "123456", true)] // la forma no se valida aquí a propósito (R13): lo hace el handler, con un 401
    public void TheValidator_OnlyDemandsPresenceAndASizeCap(string challenge, string code, bool valid)
    {
        var result = new VerifyMfaChallengeCommandValidator().Validate(new VerifyMfaChallengeCommand(challenge, code));

        Assert.Equal(valid, result.IsValid);
    }

    [Fact]
    public void TheValidator_RejectsHugeInputs_SoNothingEnormousIsHashed()
    {
        var huge = new string('a', 65);
        var validator = new VerifyMfaChallengeCommandValidator();

        Assert.False(validator.Validate(new VerifyMfaChallengeCommand(huge, "123456")).IsValid);
        Assert.False(validator.Validate(new VerifyMfaChallengeCommand("x", huge)).IsValid);
        Assert.True(validator.Validate(new VerifyMfaChallengeCommand(new string('a', 64), "123456")).IsValid); // el límite exacto vale
    }
}
