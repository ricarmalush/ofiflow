using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Mfa;

namespace OfiFlow.Application.Tests.Identity.Mfa;

/// <summary>
/// Fija los números de la política de la spec 010. No es que los valores sean sagrados: es que cambiar uno sea una
/// decisión consciente que obliga a tocar este test (y, con él, la spec y el manual) y no un cambio silencioso.
/// </summary>
public class AuthenticationPolicyTests
{
    [Fact]
    public void TheLockoutPolicy_IsFiveFailuresForFifteenMinutes()
    {
        Assert.Equal(5, AuthenticationPolicy.LockoutThreshold);
        Assert.Equal(TimeSpan.FromMinutes(15), AuthenticationPolicy.LockoutDuration);
    }

    [Fact]
    public void TheChallengePolicy_IsFiveMinutesAndFiveWrongCodes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), AuthenticationPolicy.ChallengeLifetime);
        Assert.Equal(5, AuthenticationPolicy.ChallengeMaxFailedAttempts);
    }

    [Fact]
    public void TheRecoveryPolicy_IsTenCodesOfEightyBits()
    {
        Assert.Equal(10, AuthenticationPolicy.RecoveryCodeCount);
        Assert.Equal(16, RecoveryCodes.Length); // 16 símbolos × 5 bits = 80 bits
    }

    [Fact]
    public void TheTotpParameters_AreTheOnesEveryAuthenticatorAppAssumes()
    {
        Assert.Equal(6, Totp.Digits);
        Assert.Equal(30, Totp.StepSeconds);
        Assert.Equal(1, Totp.Window); // ±30 s
        Assert.Equal(20, TotpSecret.ByteLength);
        Assert.Equal("OfiFlow", AuthenticationPolicy.TotpIssuer);
    }
}
