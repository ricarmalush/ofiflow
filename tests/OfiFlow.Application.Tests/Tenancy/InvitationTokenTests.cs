using OfiFlow.Application.Tenancy;

namespace OfiFlow.Application.Tests.Tenancy;

public class InvitationTokenTests
{
    [Fact]
    public void Generate_ReturnsA43CharacterUrlSafeValue_AndA44CharacterHash()
    {
        var (value, hash) = InvitationToken.Generate();

        Assert.Equal(43, InvitationToken.ValueLength);
        Assert.Equal(43, value.Length);
        Assert.Equal(44, hash.Length);
        // Apto para ir en un enlace: sin los caracteres de base64 normal que obligan a escapar.
        Assert.DoesNotContain('+', value);
        Assert.DoesNotContain('/', value);
        Assert.DoesNotContain('=', value);
    }

    [Fact]
    public void Generate_ReturnsADifferentTokenEveryTime()
    {
        // 256 bits aleatorios: en mil intentos no puede haber ni una repetición.
        var values = Enumerable.Range(0, 1000).Select(_ => InvitationToken.Generate().Value).ToList();

        Assert.Equal(values.Count, values.Distinct().Count());
    }

    [Fact]
    public void TheHash_IsWhatHashReturnsForTheValue_AndIsNotTheValue()
    {
        var (value, hash) = InvitationToken.Generate();

        Assert.Equal(hash, InvitationToken.Hash(value));
        Assert.NotEqual(value, hash);
        Assert.DoesNotContain(value, hash);
    }

    [Fact]
    public void Hash_IsDeterministic_AndSensitiveToAnyChange()
    {
        // Determinista: hace falta para buscar el token por su hash. Y distinto ante el mínimo cambio.
        Assert.Equal(InvitationToken.Hash("abc"), InvitationToken.Hash("abc"));
        Assert.NotEqual(InvitationToken.Hash("abc"), InvitationToken.Hash("abd"));
        Assert.NotEqual(InvitationToken.Hash("abc"), InvitationToken.Hash("ABC"));
    }

    [Fact]
    public void AGeneratedToken_IsWellFormed()
    {
        Assert.True(InvitationToken.IsWellFormed(InvitationToken.Generate().Value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("corto")]
    [InlineData("   ")]
    public void IsWellFormed_RejectsNullEmptyAndShortValues(string? value)
    {
        Assert.False(InvitationToken.IsWellFormed(value));
    }

    [Fact]
    public void IsWellFormed_RejectsAnyValueOfTheWrongLength()
    {
        var valid = InvitationToken.Generate().Value;

        Assert.False(InvitationToken.IsWellFormed(valid[..^1])); // uno menos
        Assert.False(InvitationToken.IsWellFormed(valid + "A")); // uno más
        Assert.False(InvitationToken.IsWellFormed(new string('A', 100_000))); // enorme: no se procesa
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
            var valid = InvitationToken.Generate().Value;

            foreach (var position in new[] { 0, 1, valid.Length / 2, valid.Length - 1 })
            {
                var tampered = valid[..position] + intruder + valid[(position + 1)..];

                Assert.Equal(valid.Length, tampered.Length);
                Assert.False(InvitationToken.IsWellFormed(tampered), $"Aceptó un token con '{(int)intruder}' en la posición {position}: {tampered}");
            }
        }
    }

    [Fact]
    public void EveryGeneratedToken_IsWellFormed_AcrossManySamples()
    {
        // La otra cara: la comprobación estricta no debe rechazar tokens legítimos (el alfabeto incluye '-' y '_').
        for (var sample = 0; sample < 5000; sample++)
        {
            Assert.True(InvitationToken.IsWellFormed(InvitationToken.Generate().Value));
        }
    }
}
