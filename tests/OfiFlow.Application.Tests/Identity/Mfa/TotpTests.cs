using System.Text;
using OfiFlow.Application.Identity.Mfa;

namespace OfiFlow.Application.Tests.Identity.Mfa;

/// <summary>
/// El TOTP propio contra los vectores de prueba oficiales (spec 010 R1). Son las "pruebas de oro": los valores los
/// publican las RFC, no los hemos calculado nosotros con el mismo código que se prueba. Si algún día cambia una
/// máscara, un desplazamiento o el orden de los bytes, fallan aquí con el valor esperado concreto.
/// </summary>
public class TotpTests
{
    // El secreto de los vectores de las dos RFC: los 20 caracteres ASCII "12345678901234567890".
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    // ===== RFC 4226, Apéndice D: los 10 primeros valores de HOTP (6 dígitos) =====

    [Theory]
    [InlineData(0, "755224")]
    [InlineData(1, "287082")]
    [InlineData(2, "359152")]
    [InlineData(3, "969429")]
    [InlineData(4, "338314")]
    [InlineData(5, "254676")]
    [InlineData(6, "287922")]
    [InlineData(7, "162583")]
    [InlineData(8, "399871")]
    [InlineData(9, "520489")]
    public void Compute_MatchesTheRfc4226Appendix_D_Vectors(long counter, string expected)
    {
        Assert.Equal(expected, Totp.Compute(RfcSecret, counter));
    }

    // ===== RFC 6238, Apéndice B (HMAC-SHA1, 8 dígitos): instante → código =====

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Compute_MatchesTheRfc6238AppendixB_Vectors_AtEightDigits(long unixSeconds, string expected)
    {
        var step = Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        Assert.Equal(expected, Totp.Compute(RfcSecret, step, digits: 8));
    }

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Compute_AtSixDigits_IsTheLastSixOfTheRfcValue_KeepingLeadingZeros(long unixSeconds, string expected)
    {
        // Son las mismas filas de la RFC con 6 dígitos (los 6 últimos del valor de 8). Las que empiezan por 0
        // comprueban que el código se rellena con ceros a la izquierda y no pierde un dígito.
        var step = Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        Assert.Equal(expected, Totp.Compute(RfcSecret, step));
        Assert.Equal(Totp.Digits, expected.Length);
    }

    [Fact]
    public void StepAt_ChangesExactlyEveryThirtySeconds()
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(1_700_000_010); // múltiplo de 30

        Assert.Equal(Totp.StepAt(start), Totp.StepAt(start.AddSeconds(29)));
        Assert.Equal(Totp.StepAt(start) + 1, Totp.StepAt(start.AddSeconds(30)));
        Assert.Equal(Totp.StepAt(start) - 1, Totp.StepAt(start.AddSeconds(-1)));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(9)]
    public void Compute_RejectsADigitCountOutsideTheSupportedRange(int digits)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Totp.Compute(RfcSecret, 1, digits));
    }

    // ===== Verificación (ventana, repetición, forma) =====

    // Un instante cualquiera, con su paso ya conocido para escribir los casos en términos de "paso N".
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_015);
    private static readonly long CurrentStep = Totp.StepAt(Now);

    private static string CodeAt(long step) => Totp.Compute(RfcSecret, step);

    [Fact]
    public void Verify_AcceptsTheCurrentStep_AndReportsWhichOneItWas()
    {
        var result = Totp.Verify(RfcSecret, CodeAt(CurrentStep), Now, lastAcceptedStep: 0);

        Assert.Equal(new TotpVerification(TotpOutcome.Valid, CurrentStep), result);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void Verify_AcceptsOneStepOnEachSide_ForClockDrift(long offset)
    {
        var result = Totp.Verify(RfcSecret, CodeAt(CurrentStep + offset), Now, lastAcceptedStep: 0);

        Assert.Equal(TotpOutcome.Valid, result.Outcome);
        Assert.Equal(CurrentStep + offset, result.Step);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(2)]
    [InlineData(-10)]
    public void Verify_RejectsTwoOrMoreStepsAway(long offset)
    {
        var result = Totp.Verify(RfcSecret, CodeAt(CurrentStep + offset), Now, lastAcceptedStep: 0);

        Assert.Equal(TotpOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public void Verify_RejectsTheSameStepTwice_ReportedAsReplayed()
    {
        var first = Totp.Verify(RfcSecret, CodeAt(CurrentStep), Now, lastAcceptedStep: 0);
        Assert.Equal(TotpOutcome.Valid, first.Outcome);

        // El mismo código, dentro de su ventana, con el último paso aceptado ya anotado.
        var second = Totp.Verify(RfcSecret, CodeAt(CurrentStep), Now, lastAcceptedStep: first.Step);

        Assert.Equal(TotpOutcome.Replayed, second.Outcome);
    }

    [Fact]
    public void Verify_RejectsAnEarlierStepOnceALaterOneWasAccepted()
    {
        // Se aceptó el paso siguiente (móvil adelantado); el actual y el anterior ya no valen aunque estén en la ventana.
        Assert.Equal(TotpOutcome.Replayed, Totp.Verify(RfcSecret, CodeAt(CurrentStep), Now, CurrentStep + 1).Outcome);
        Assert.Equal(TotpOutcome.Replayed, Totp.Verify(RfcSecret, CodeAt(CurrentStep - 1), Now, CurrentStep + 1).Outcome);
        // Control positivo: el último paso de la ventana, que es posterior, sí.
        Assert.Equal(TotpOutcome.Valid, Totp.Verify(RfcSecret, CodeAt(CurrentStep + 1), Now, CurrentStep).Outcome);
    }

    [Fact]
    public void Verify_AWrongCode_IsInvalid_NotReplayed()
    {
        var wrong = CodeAt(CurrentStep) == "000000" ? "000001" : "000000";

        Assert.Equal(TotpOutcome.Invalid, Totp.Verify(RfcSecret, wrong, Now, lastAcceptedStep: 0).Outcome);
    }

    [Fact]
    public void Verify_ACodeFromAnotherSecret_IsInvalid()
    {
        var otherSecret = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOPQRST");

        // Control: con el secreto correcto, el mismo paso sí valdría.
        Assert.Equal(TotpOutcome.Valid, Totp.Verify(RfcSecret, CodeAt(CurrentStep), Now, 0).Outcome);
        Assert.Equal(TotpOutcome.Invalid, Totp.Verify(otherSecret, CodeAt(CurrentStep), Now, 0).Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData("12 456")]
    [InlineData(" 12345")]
    [InlineData("１２３４５６")] // dígitos de ancho completo: son "dígitos" Unicode pero no ASCII
    [InlineData("-12345")]
    public void Verify_ATextThatIsNotExactlySixAsciiDigits_IsInvalid_WithoutComputingAnything(string? code)
    {
        Assert.Equal(TotpOutcome.Invalid, Totp.Verify(RfcSecret, code, Now, lastAcceptedStep: 0).Outcome);
        Assert.False(Totp.IsWellFormed(code));
    }

    [Fact]
    public void Verify_AValidCodeWithTheRightShape_IsWellFormed()
    {
        Assert.True(Totp.IsWellFormed("000000"));
        Assert.True(Totp.IsWellFormed("123456"));
    }

    [Fact]
    public void Verify_NearTheStartOfTheEpoch_DoesNotComputeNegativeSteps()
    {
        // Instante 0: el paso anterior sería el -1. No debe lanzar ni aceptar nada raro.
        var epoch = DateTimeOffset.FromUnixTimeSeconds(0);

        var result = Totp.Verify(RfcSecret, CodeAt(0), epoch, lastAcceptedStep: -1);

        Assert.Equal(new TotpVerification(TotpOutcome.Valid, 0), result);
    }

    [Fact]
    public void ARealisticSecret_WorksEndToEnd_WithTheRandomGenerator()
    {
        var secret = TotpSecret.Generate();
        var code = Totp.Compute(secret, CurrentStep);

        Assert.Equal(TotpOutcome.Valid, Totp.Verify(secret, code, Now, 0).Outcome);
    }
}
