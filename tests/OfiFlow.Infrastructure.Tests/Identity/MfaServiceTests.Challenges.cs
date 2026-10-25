using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Security;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Mfa;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// El desafío del login en dos pasos (spec 010, bloque 5), contra SQL Server real: crearlo, canjearlo una sola vez, que
/// muera a los 5 intentos o a los 5 minutos, y que de varios canjes simultáneos solo gane uno. Comparte las ayudas de
/// <see cref="MfaServiceTests"/>.
/// </summary>
public partial class MfaServiceTests
{
    private async Task<string> NewChallengeAsync(Guid userId, FakeTimeProvider clock, ChallengeAudience audience = ChallengeAudience.Tenant)
    {
        var (value, hash) = OpaqueToken.Generate();

        await using var db = Open();
        await Service(db, clock).CreateChallengeAsync(userId, hash, clock.GetUtcNow().UtcDateTime + AuthenticationPolicy.ChallengeLifetime, audience, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        return value;
    }

    private async Task<ChallengeRedemption> RedeemAsync(string challenge, string code, FakeTimeProvider clock, ListLogger<MfaService>? logger = null, ChallengeAudience audience = ChallengeAudience.Tenant)
    {
        await using var db = Open();
        return await Service(db, clock, logger).RedeemChallengeAsync(OpaqueToken.Hash(challenge), code, audience, CancellationToken.None);
    }

    private async Task<(int Failed, DateTimeOffset? LockedUntil)> AccountStateAsync(Guid id)
    {
        await using var db = Open();
        var user = await db.ApplicationUsers.AsNoTracking().SingleAsync(u => u.Id == id);

        return (user.AccessFailedCount, user.LockoutEnd);
    }

    private async Task SetCounterAsync(Guid id, int failed)
    {
        await using var db = Open();
        await db.ApplicationUsers.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.AccessFailedCount, failed));
    }

    [Fact]
    public async Task CreatingAChallenge_StoresOnlyItsHash_AndExpiresInFiveMinutes()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, _, _) = await SeedEnabledAsync(clock);

        var challenge = await NewChallengeAsync(id, clock);

        await using var read = Open();
        var stored = await read.MfaChallenges.AsNoTracking().SingleAsync(c => c.UserId == id);
        Assert.Equal(OpaqueToken.Hash(challenge), stored.TokenHash);
        Assert.NotEqual(challenge, stored.TokenHash);
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddMinutes(5), stored.ExpiresAt);
        Assert.Equal(0, stored.FailedAttempts);
        Assert.Null(stored.ConsumedAt);
    }

    [Fact]
    public async Task CreatingAChallenge_CleansTheUselessOnesOfThatAccountOnly()
    {
        var clock = new FakeTimeProvider(Start);
        var (ana, _, _) = await SeedEnabledAsync(clock);
        var (luis, _, _) = await SeedEnabledAsync(clock);

        var expired = await NewChallengeAsync(ana, clock);
        var luisOld = await NewChallengeAsync(luis, clock);
        clock.Advance(TimeSpan.FromMinutes(6)); // el de Ana y el de Luis caducan

        var fresh = await NewChallengeAsync(ana, clock);

        await using var read = Open();
        var anas = await read.MfaChallenges.AsNoTracking().Where(c => c.UserId == ana).Select(c => c.TokenHash).ToListAsync();
        Assert.Equal([OpaqueToken.Hash(fresh)], anas); // el caducado de Ana desapareció
        Assert.DoesNotContain(OpaqueToken.Hash(expired), anas);

        // El de Luis, caducado también, sigue ahí: solo se limpia la cuenta que crea un desafío nuevo.
        Assert.Contains(OpaqueToken.Hash(luisOld), await read.MfaChallenges.AsNoTracking().Where(c => c.UserId == luis).Select(c => c.TokenHash).ToListAsync());
    }

    [Fact]
    public async Task AGoodRedemption_Succeeds_ConsumesTheChallenge_AndClearsTheAccountCounter()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var challenge = await NewChallengeAsync(id, clock);
        await SetCounterAsync(id, 3); // un acceso completo lo deja a cero (R17)

        var result = await RedeemAsync(challenge, CodeFor(secret, clock), clock);

        Assert.True(result.Succeeded);
        Assert.Equal(id, result.UserId);
        Assert.Equal((0, (DateTimeOffset?)null), await AccountStateAsync(id));

        await using var read = Open();
        Assert.NotNull((await read.MfaChallenges.AsNoTracking().SingleAsync(c => c.UserId == id)).ConsumedAt);
    }

    [Fact]
    public async Task ARedeemedChallenge_CannotBeRedeemedAgain_EvenWithAFreshValidCode()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var challenge = await NewChallengeAsync(id, clock);

        Assert.True((await RedeemAsync(challenge, CodeFor(secret, clock), clock)).Succeeded);

        clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds)); // un código nuevo, válido
        var again = await RedeemAsync(challenge, CodeFor(secret, clock), clock);

        Assert.False(again.Succeeded); // un solo uso
    }

    [Fact]
    public async Task AWrongCode_IsRejected_SpendsOneAttemptOfTheChallenge_AndCountsAgainstTheAccount()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var challenge = await NewChallengeAsync(id, clock);
        var wrong = CodeFor(secret, clock) == "000000" ? "000001" : "000000";

        Assert.False((await RedeemAsync(challenge, wrong, clock)).Succeeded);

        await using (var read = Open())
        {
            Assert.Equal(1, (await read.MfaChallenges.AsNoTracking().SingleAsync(c => c.UserId == id)).FailedAttempts);
        }

        Assert.Equal(1, (await AccountStateAsync(id)).Failed);

        // Control: el mismo desafío sigue valiendo con el código bueno (un fallo no lo mata).
        Assert.True((await RedeemAsync(challenge, CodeFor(secret, clock), clock)).Succeeded);
    }

    [Fact]
    public async Task AfterFiveWrongCodes_TheChallengeIsDead_EvenIfTheAccountWereNotLocked()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var challenge = await NewChallengeAsync(id, clock);
        var wrong = CodeFor(secret, clock) == "000000" ? "000001" : "000000";
        var logger = new ListLogger<MfaService>();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.False((await RedeemAsync(challenge, wrong, clock)).Succeeded);
        }

        // Se levanta el bloqueo de la CUENTA a propósito, para aislar el tope del DESAFÍO.
        await using (var unlock = Open())
        {
            await unlock.ApplicationUsers.Where(u => u.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.AccessFailedCount, 0).SetProperty(u => u.LockoutEnd, (DateTimeOffset?)null));
        }

        var result = await RedeemAsync(challenge, CodeFor(secret, clock), clock, logger);

        Assert.False(result.Succeeded); // con el código bueno, pero el desafío ya gastó sus 5 intentos
        Assert.Contains(logger.Entries, e => e.EventId.Id == SecurityEventIds.MfaVerificationFailed && e.Message.Contains("ChallengeExhausted"));
    }

    [Fact]
    public async Task AnExpiredChallenge_IsRejected_AndDoesNotTouchTheAccountCounter()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var challenge = await NewChallengeAsync(id, clock);
        var logger = new ListLogger<MfaService>();

        clock.Advance(TimeSpan.FromMinutes(5)); // justo en el límite: ya no vale

        Assert.False((await RedeemAsync(challenge, CodeFor(secret, clock), clock, logger)).Succeeded);
        Assert.Equal(0, (await AccountStateAsync(id)).Failed); // un desafío caducado no cuenta como intento de adivinar
        Assert.Contains(logger.Entries, e => e.Message.Contains("ChallengeExpired"));
    }

    [Fact]
    public async Task AChallengeJustBeforeItExpires_StillWorks()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var challenge = await NewChallengeAsync(id, clock);

        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1)); // control positivo del límite

        Assert.True((await RedeemAsync(challenge, CodeFor(secret, clock), clock)).Succeeded);
    }

    [Fact]
    public async Task AnUnknownChallenge_IsRejected_AndNothingChanges()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var logger = new ListLogger<MfaService>();

        var result = await RedeemAsync(OpaqueToken.Generate().Value, CodeFor(secret, clock), clock, logger);

        Assert.False(result.Succeeded);
        Assert.Equal(0, (await AccountStateAsync(id)).Failed); // inventar desafíos no puede bloquear cuentas ajenas
        Assert.Contains(logger.Entries, e => e.Message.Contains("ChallengeUnknown"));
    }

    [Fact]
    public async Task AChallengeOfAnAccountWhoseMfaWasDisabledMeanwhile_IsRejected()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var challenge = await NewChallengeAsync(id, clock);

        await using (var db = Open())
        {
            await Service(db, clock).RemoveAllAsync(id, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        Assert.False((await RedeemAsync(challenge, CodeFor(secret, clock), clock)).Succeeded);
    }

    [Fact]
    public async Task ARecoveryCode_RedeemsTheChallenge_AndIsConsumed()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, _, codes) = await SeedEnabledAsync(clock);
        var challenge = await NewChallengeAsync(id, clock);

        var result = await RedeemAsync(challenge, codes[0].Code, clock);

        Assert.True(result.Succeeded);
        await using var read = Open();
        Assert.Equal(9, await read.RecoveryCodes.CountAsync(c => c.UserId == id && c.UsedAt == null));
    }

    [Fact]
    public async Task AReplayedTotpCode_CannotRedeemANewChallenge()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var code = CodeFor(secret, clock);

        Assert.True((await RedeemAsync(await NewChallengeAsync(id, clock), code, clock)).Succeeded);

        // Quien viera el código por encima del hombro no puede usarlo en otro desafío: el paso ya se gastó.
        Assert.False((await RedeemAsync(await NewChallengeAsync(id, clock), code, clock)).Succeeded);
    }

    [Fact]
    public async Task AChallengeOfOneAccount_CannotBeRedeemedWithTheCodeOfAnother()
    {
        var clock = new FakeTimeProvider(Start);
        var (ana, _, _) = await SeedEnabledAsync(clock);
        var (_, luisSecret, _) = await SeedEnabledAsync(clock);
        var anasChallenge = await NewChallengeAsync(ana, clock);

        var result = await RedeemAsync(anasChallenge, CodeFor(luisSecret, clock), clock);

        Assert.False(result.Succeeded);
        Assert.Equal(1, (await AccountStateAsync(ana)).Failed); // el fallo cuenta contra la cuenta del desafío
    }

    [Fact]
    public async Task ALockedAccount_CannotRedeem_EvenWithAValidChallengeAndAValidCode()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var challenge = await NewChallengeAsync(id, clock);
        var wrong = CodeFor(secret, clock) == "000000" ? "000001" : "000000";

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await VerifyAsync(id, wrong, clock); // bloquea la cuenta por otra vía
        }

        Assert.False((await RedeemAsync(challenge, CodeFor(secret, clock), clock)).Succeeded);
    }

    [Fact]
    public async Task FourSimultaneousRedemptions_OfTheSameChallengeAndCode_ProduceExactlyOneSession()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var challenge = await NewChallengeAsync(id, clock);
        var code = CodeFor(secret, clock);

        using var gate = new ManualResetEventSlim(false);
        var attempts = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            gate.Wait();
            return await RedeemAsync(challenge, code, clock);
        })).ToList();

        gate.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result.Succeeded); // R11: de cuatro canjes a la vez, solo uno gana
    }

    [Fact]
    public async Task TwoSimultaneousRedemptions_WithTwoDifferentValidCodes_ProduceExactlyOneSession()
    {
        // Aunque el código no sea el mismo (dos pasos distintos, ambos válidos en la ventana), el DESAFÍO se canjea
        // una sola vez: la sentencia que lo consume es condicional.
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var challenge = await NewChallengeAsync(id, clock);
        var codes = new[] { CodeFor(secret, clock, stepOffset: 0), CodeFor(secret, clock, stepOffset: 1) };

        using var gate = new ManualResetEventSlim(false);
        var attempts = codes.Select(code => Task.Run(async () =>
        {
            gate.Wait();
            return await RedeemAsync(challenge, code, clock);
        })).ToList();

        gate.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result.Succeeded);
    }

    [Fact]
    public async Task TheRedemptionEvents_NeverContainTheChallengeTheCodeNorTheirHashes()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, codes) = await SeedEnabledAsync(clock);
        var challenge = await NewChallengeAsync(id, clock);
        var logger = new ListLogger<MfaService>();
        var wrong = CodeFor(secret, clock) == "000000" ? "000001" : "000000";

        await RedeemAsync(challenge, wrong, clock, logger);
        await RedeemAsync(OpaqueToken.Generate().Value, wrong, clock, logger);
        await RedeemAsync(challenge, codes[0].Code, clock, logger);

        Assert.NotEmpty(logger.Entries);
        Assert.All(logger.Entries, entry =>
        {
            Assert.DoesNotContain(challenge, entry.Message);
            Assert.DoesNotContain(OpaqueToken.Hash(challenge), entry.Message);
            Assert.DoesNotContain(wrong, entry.Message);
            Assert.DoesNotContain(codes[0].Code, entry.Message);
        });
    }

    // ===== Un desafío no cruza de un mundo a otro (spec 011 R9) =====

    [Fact]
    public async Task ATenantChallenge_RedeemedAsPlatform_IsRejected_EvenWithAValidCode()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var challenge = await NewChallengeAsync(id, clock, ChallengeAudience.Tenant);

        var result = await RedeemAsync(challenge, CodeFor(secret, clock), clock, audience: ChallengeAudience.Platform);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task APlatformChallenge_RedeemedAsTenant_IsRejected_EvenWithAValidCode()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var challenge = await NewChallengeAsync(id, clock, ChallengeAudience.Platform);

        var result = await RedeemAsync(challenge, CodeFor(secret, clock), clock, audience: ChallengeAudience.Tenant);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task APlatformChallenge_RedeemedAsPlatform_Succeeds()
    {
        // Control positivo: sin él, los dos tests de arriba pasarían aunque los desafíos de plataforma no se pudieran canjear nunca.
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var challenge = await NewChallengeAsync(id, clock, ChallengeAudience.Platform);

        var result = await RedeemAsync(challenge, CodeFor(secret, clock), clock, audience: ChallengeAudience.Platform);

        Assert.True(result.Succeeded);
        Assert.Equal(id, result.UserId);
    }

    [Fact]
    public async Task ARedemptionInTheWrongWorld_SpendsNothing_NeitherAnAttemptNorTheCode()
    {
        // Se trata como un desafío desconocido, ANTES de comprobar el código: no cuenta contra la cuenta (si contara, cualquiera
        // podría bloquear una cuenta ajena con desafíos del otro mundo) y no gasta el código, que sigue valiendo en el suyo.
        var clock = new FakeTimeProvider(Start);
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var challenge = await NewChallengeAsync(id, clock, ChallengeAudience.Tenant);
        var code = CodeFor(secret, clock);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            Assert.False((await RedeemAsync(challenge, code, clock, audience: ChallengeAudience.Platform)).Succeeded);
        }

        var (failed, lockedUntil) = await AccountStateAsync(id);
        Assert.Equal(0, failed); // ningún intento gastado, y por tanto ni un bloqueo
        Assert.Null(lockedUntil);
        await using (var read = Open())
        {
            Assert.Equal(0, (await read.MfaChallenges.AsNoTracking().SingleAsync(c => c.UserId == id)).FailedAttempts); // ni el desafío
        }

        // El código nunca llegó a comprobarse, así que sigue valiendo en su mundo.
        Assert.True((await RedeemAsync(challenge, code, clock, audience: ChallengeAudience.Tenant)).Succeeded);
    }

    [Fact]
    public async Task ARedemptionInTheWrongWorld_LogsTheRealReason_WithoutTheChallengeOrTheCode()
    {
        var clock = new FakeTimeProvider(Start);
        var logger = new ListLogger<MfaService>();
        var (id, secret, _) = await SeedEnabledAsync(clock);
        var challenge = await NewChallengeAsync(id, clock, ChallengeAudience.Tenant);
        var code = CodeFor(secret, clock);

        await RedeemAsync(challenge, code, clock, logger, ChallengeAudience.Platform);

        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("ChallengeWrongAudience"));
        Assert.Contains(id.ToString(), entry.Message);
        Assert.DoesNotContain(challenge, entry.Message);
        Assert.DoesNotContain(code, entry.Message);
    }

    [Fact]
    public async Task TheAudience_IsStoredWithTheChallenge()
    {
        var clock = new FakeTimeProvider(Start);
        var (id, _, _) = await SeedEnabledAsync(clock);

        await NewChallengeAsync(id, clock, ChallengeAudience.Platform);

        await using var read = Open();
        Assert.Equal(ChallengeAudience.Platform, (await read.MfaChallenges.AsNoTracking().SingleAsync(c => c.UserId == id)).Audience);
    }
}
