using OfiFlow.Application.Common.Security;
using OfiFlow.Application.Tenancy;

namespace OfiFlow.Application.Tests.Common;

public class OpaqueTokenTests
{
    [Fact]
    public void Generate_ReturnsA43CharacterUrlSafeValue_AndA44CharacterHash()
    {
        var (value, hash) = OpaqueToken.Generate();

        Assert.Equal(43, OpaqueToken.ValueLength);
        Assert.Equal(43, value.Length);
        Assert.Equal(44, hash.Length);
        // Apto para ir en un enlace: sin los caracteres de base64 normal que obligan a escapar.
        Assert.DoesNotContain('+', value);
        Assert.DoesNotContain('/', value);
        Assert.DoesNotContain('=', value);
    }

    [Fact]
    public void ATokenIsNotAJwt_SoItCanNeverBePresentedAsAnAccessToken()
    {
        // El desafío del login en dos pasos usa este mismo generador. Que no sea un JWT (tres partes con puntos y
        // cabecera JSON) es lo que hace imposible aceptarlo, ni por un error de audiencia, como token de acceso.
        for (var i = 0; i < 200; i++)
        {
            var (value, _) = OpaqueToken.Generate();

            Assert.DoesNotContain('.', value);
            Assert.NotEqual(3, value.Split('.').Length); // un JWT siempre tiene tres partes separadas por puntos
        }
    }

    [Fact]
    public void Generate_ReturnsADifferentTokenEveryTime()
    {
        // 256 bits aleatorios: en mil intentos no puede haber ni una repetición.
        var values = Enumerable.Range(0, 1000).Select(_ => OpaqueToken.Generate().Value).ToList();

        Assert.Equal(values.Count, values.Distinct().Count());
    }

    [Fact]
    public void TheHash_IsWhatHashReturnsForTheValue_AndIsNotTheValue()
    {
        var (value, hash) = OpaqueToken.Generate();

        Assert.Equal(hash, OpaqueToken.Hash(value));
        Assert.NotEqual(value, hash);
        Assert.DoesNotContain(value, hash);
    }

    [Fact]
    public void Hash_IsDeterministic_AndSensitiveToAnyChange()
    {
        // Determinista: hace falta para buscar el token por su hash. Y distinto ante el mínimo cambio.
        Assert.Equal(OpaqueToken.Hash("abc"), OpaqueToken.Hash("abc"));
        Assert.NotEqual(OpaqueToken.Hash("abc"), OpaqueToken.Hash("abd"));
        Assert.NotEqual(OpaqueToken.Hash("abc"), OpaqueToken.Hash("ABC"));
    }

    [Fact]
    public void AGeneratedToken_IsWellFormed()
    {
        Assert.True(OpaqueToken.IsWellFormed(OpaqueToken.Generate().Value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("corto")]
    [InlineData("   ")]
    public void IsWellFormed_RejectsNullEmptyAndShortValues(string? value)
    {
        Assert.False(OpaqueToken.IsWellFormed(value));
    }

    [Fact]
    public void IsWellFormed_RejectsAnyValueOfTheWrongLength()
    {
        var valid = OpaqueToken.Generate().Value;

        Assert.False(OpaqueToken.IsWellFormed(valid[..^1])); // uno menos
        Assert.False(OpaqueToken.IsWellFormed(valid + "A")); // uno más
        Assert.False(OpaqueToken.IsWellFormed(new string('A', 100_000))); // enorme: no se procesa
    }

    [Theory]
    [InlineData('+')]
    [InlineData('/')]
    [InlineData('=')]
    [InlineData(' ')]
    [InlineData('\t')]
    [InlineData('\n')]
    [InlineData('ñ')]
    [InlineData('.')]
    public void IsWellFormed_RejectsCharactersOutsideTheUrlSafeAlphabet_InAnyPositionOfAnyToken(char intruder)
    {
        // Muchos tokens y varias posiciones, no una sola muestra: el resultado de alguno de estos casos dependía del
        // azar del token (Base64Url.IsValid ignora los espacios y acepta o no según el último carácter).
        for (var sample = 0; sample < 300; sample++)
        {
            var valid = OpaqueToken.Generate().Value;

            foreach (var position in new[] { 0, 1, valid.Length / 2, valid.Length - 1 })
            {
                var tampered = valid[..position] + intruder + valid[(position + 1)..];

                Assert.Equal(valid.Length, tampered.Length);
                Assert.False(OpaqueToken.IsWellFormed(tampered), $"Aceptó un token con '{(int)intruder}' en la posición {position}: {tampered}");
            }
        }
    }

    [Fact]
    public void EveryGeneratedToken_IsWellFormed_AcrossManySamples()
    {
        // La otra cara: la comprobación estricta no debe rechazar tokens legítimos (el alfabeto incluye '-' y '_').
        for (var sample = 0; sample < 5000; sample++)
        {
            Assert.True(OpaqueToken.IsWellFormed(OpaqueToken.Generate().Value));
        }
    }
}
