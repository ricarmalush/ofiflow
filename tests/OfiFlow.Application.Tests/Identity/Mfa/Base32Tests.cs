using System.Text;
using OfiFlow.Application.Identity.Mfa;

namespace OfiFlow.Application.Tests.Identity.Mfa;

public class Base32Tests
{
    // Vectores de prueba de la RFC 4648 §10, sin el relleno ("="): las apps lo aceptan sin él.
    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Encode_MatchesTheRfc4648TestVectors(string plain, string expected)
    {
        Assert.Equal(expected, Base32.Encode(Encoding.ASCII.GetBytes(plain)));
    }

    [Fact]
    public void Encode_OfA20ByteSecret_Is32CharactersOfTheAlphabetOnly()
    {
        // 20 bytes = 160 bits = exactamente 32 caracteres de 5 bits, sin relleno que quitar.
        var encoded = Base32.Encode(TotpSecret.Generate());

        Assert.Equal(32, encoded.Length);
        Assert.All(encoded, character => Assert.True(character is (>= 'A' and <= 'Z') or (>= '2' and <= '7'), $"'{character}' no es del alfabeto"));
    }

    [Fact]
    public void Encode_OfAllZeroAndAllOneBytes_UsesTheFirstAndLastSymbols()
    {
        // Controles de los extremos del alfabeto: fallarían si un desplazamiento o una máscara estuviera mal.
        Assert.Equal("AAAAAAAA", Base32.Encode(new byte[5]));
        Assert.Equal("77777777", Base32.Encode([0xFF, 0xFF, 0xFF, 0xFF, 0xFF]));
    }
}
