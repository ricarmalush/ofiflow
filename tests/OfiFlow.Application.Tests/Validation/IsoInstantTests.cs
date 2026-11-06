using System.Globalization;
using OfiFlow.Application.Common.Validation;

namespace OfiFlow.Application.Tests.Validation;

/// <summary>
/// El instante con desfase explícito de la agenda (spec 020 R4). Lo importante es lo que se NIEGA a leer: una hora sin desfase
/// daría la hora local del servidor, un error silencioso de horas en una cita.
/// </summary>
public class IsoInstantTests
{
    // 09:00 en Madrid en noviembre (UTC+1) son las 08:00 UTC.
    [Theory]
    [InlineData("2026-11-10T08:00:00Z", 2026, 11, 10, 8, 0, 0)]
    [InlineData("2026-11-10T09:00:00+01:00", 2026, 11, 10, 8, 0, 0)]
    [InlineData("2026-11-10T03:00:00-05:00", 2026, 11, 10, 8, 0, 0)]
    [InlineData("2026-11-10T13:30:00+05:30", 2026, 11, 10, 8, 0, 0)]
    [InlineData("2026-11-10T08:00Z", 2026, 11, 10, 8, 0, 0)]
    [InlineData("2026-11-10T00:30:00+01:00", 2026, 11, 9, 23, 30, 0)]
    public void ReadsTheInstantAndConvertsItToUtc(string text, int year, int month, int day, int hour, int minute, int second)
    {
        Assert.True(IsoInstant.TryParse(text, out var utc));

        Assert.Equal(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc), utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Theory]
    [InlineData("2026-11-10T08:00:00.000Z", 0)]
    [InlineData("2026-11-10T08:00:00.5Z", 5_000_000)]
    [InlineData("2026-11-10T08:00:00.1234567Z", 1_234_567)]
    public void AcceptsFractionsOfASecond_TheOnesTheBrowserSends(string text, long ticks)
    {
        Assert.True(IsoInstant.TryParse(text, out var utc));

        Assert.Equal(new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc).AddTicks(ticks), utc);
    }

    [Theory]
    [InlineData("2026-11-10T09:00:00")]            // sin desfase: la hora local del servidor, que no es la de quien la manda
    [InlineData("2026-11-10T09:00")]
    [InlineData("2026-11-10")]                     // solo fecha
    [InlineData("2026-11-10 09:00:00Z")]           // espacio en vez de T
    [InlineData("2026-11-10T09:00:00+0100")]       // desfase sin dos puntos
    [InlineData("2026-11-10T09:00:00+01")]
    [InlineData("2026-11-10T09:00:00z")]           // z minúscula: no es la forma pedida
    [InlineData("2026-11-10T09:00:00Z ")]          // espacio al final
    [InlineData(" 2026-11-10T09:00:00Z")]
    [InlineData("2026-11-10T09:00:00Zjunk")]
    [InlineData("2026-11-10T09:00:00.Z")]          // punto sin dígitos
    [InlineData("2026-11-10T09:00:00.12345678Z")]  // ocho dígitos de fracción
    [InlineData("2026-13-10T09:00:00Z")]           // mes 13
    [InlineData("2026-02-30T09:00:00Z")]           // 30 de febrero
    [InlineData("2026-11-10T24:00:00Z")]           // hora 24
    [InlineData("2026-11-10T09:60:00Z")]
    [InlineData("10/11/2026 09:00:00 +01:00")]
    [InlineData("hoy")]
    [InlineData("")]
    [InlineData("   ")]
    public void RefusesAnythingElse(string text)
    {
        Assert.False(IsoInstant.TryParse(text, out _));
    }

    [Fact]
    public void ATrailingLineBreak_IsRefused_ThePatternEndsWithBackslashZ()
    {
        // «$» aceptaría un salto de línea final; \z no.
        Assert.False(IsoInstant.TryParse("2026-11-10T09:00:00Z\n", out _));
    }

    [Fact]
    public void Null_IsRefused()
    {
        Assert.False(IsoInstant.TryParse(null, out _));
    }

    // En .NET «\d» acepta los dígitos árabes: un dígito de otro alfabeto, en CUALQUIER posición, no se da por válido.
    [Theory]
    [InlineData("٢٠٢٦-١١-١٠T٠٨:٠٠:٠٠Z")]       // todo en árabe
    [InlineData("٢026-11-10T08:00:00Z")]       // solo el año
    [InlineData("2026-١1-10T08:00:00Z")]       // solo el mes
    [InlineData("2026-11-١0T08:00:00Z")]       // solo el día
    [InlineData("2026-11-10T٠8:00:00Z")]       // solo la hora
    [InlineData("2026-11-10T08:٠0:00Z")]       // solo los minutos
    [InlineData("2026-11-10T08:00:٠0Z")]       // solo los segundos
    [InlineData("2026-11-10T08:00:00.٥Z")]     // solo la fracción
    [InlineData("2026-11-10T08:00:00+٠1:00")]  // solo el desfase
    public void ADigitFromAnotherAlphabet_IsRefused_InAnyPosition(string text)
    {
        Assert.False(IsoInstant.TryParse(text, out _));
    }

    [Fact]
    public void ATextLongerThanTheLongestValidForm_IsRefusedBeforeBeingRead()
    {
        var tooLong = "2026-11-10T08:00:00." + new string('1', 30) + "Z";

        Assert.True(tooLong.Length > IsoInstant.MaxLength);
        Assert.False(IsoInstant.TryParse(tooLong, out _));
    }

    [Fact]
    public void TheLongestValidForm_FitsInTheLimit()
    {
        const string Longest = "2026-11-10T09:00:00.1234567+01:00";

        Assert.True(IsoInstant.TryParse(Longest, out _));
        Assert.True(Longest.Length <= IsoInstant.MaxLength);
    }

    // Culturas con otro calendario por defecto: el tailandés (año budista: 2026 sería 1483), el persa, el árabe saudí (Umm al-Qura).
    [Theory]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    [InlineData("fa-IR")]
    [InlineData("he-IL")]
    public void TheResult_DoesNotDependOnTheCultureOfTheServer(string culture)
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            Assert.True(IsoInstant.TryParse("2026-11-10T09:00:00+01:00", out var utc));
            Assert.Equal(new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc), utc);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Parse_WithAnInvalidText_Throws()
    {
        Assert.Throws<FormatException>(() => IsoInstant.Parse("2026-11-10T09:00:00"));
    }

    [Fact]
    public void Parse_WithAValidText_ReturnsTheUtcInstant()
    {
        Assert.Equal(new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc), IsoInstant.Parse("2026-11-10T09:00:00+01:00"));
    }
}
