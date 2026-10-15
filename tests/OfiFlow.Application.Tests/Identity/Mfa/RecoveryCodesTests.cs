using OfiFlow.Application.Identity.Mfa;

namespace OfiFlow.Application.Tests.Identity.Mfa;

public class RecoveryCodesTests
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    [Fact]
    public void Generate_HasTheFormatXXXX_XXXX_XXXX_XXXX_WithOnlyTheUnambiguousAlphabet()
    {
        var code = RecoveryCodes.Generate();

        Assert.Equal(19, code.Length);
        Assert.Equal([4, 4, 4, 4], code.Split('-').Select(group => group.Length));
        Assert.All(code.Replace("-", ""), character => Assert.Contains(character, Alphabet));
        // Ninguno de los caracteres que se confunden al copiar a mano.
        Assert.DoesNotContain(code, character => character is 'I' or 'L' or 'O' or 'U');
    }

    [Fact]
    public void Generate_ReturnsADifferentCodeEveryTime_AndEverySymbolOccurs()
    {
        // 80 bits: ni una repetición en 2000 códigos. Y con 32.000 símbolos generados salen los 32 del alfabeto
        // (un generador sesgado o atascado en pocos símbolos lo delataría).
        var codes = Enumerable.Range(0, 2000).Select(_ => RecoveryCodes.Generate()).ToList();

        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.Equal(Alphabet.Length, codes.SelectMany(code => code.Replace("-", "")).Distinct().Count());
    }

    [Fact]
    public void Normalize_OfAGeneratedCode_GivesTheSixteenSymbolsWithoutHyphens()
    {
        var code = RecoveryCodes.Generate();

        Assert.Equal(code.Replace("-", ""), RecoveryCodes.Normalize(code));
    }

    [Theory]
    [InlineData("abcd-efgh-jkmn-pqrs", "ABCDEFGHJKMNPQRS")] // minúsculas
    [InlineData("ABCDEFGHJKMNPQRS", "ABCDEFGHJKMNPQRS")] // sin guiones
    [InlineData("ABCD-EFGH-JKMN-PQRS", "ABCDEFGHJKMNPQRS")]
    [InlineData("ABCDEFGH-JKMNPQRS", "ABCDEFGHJKMNPQRS")] // guiones donde sea
    [InlineData("0000-1111-0000-1111", "0000111100001111")]
    [InlineData("OOOO-IIII-LLLL-oilO", "0000111111110110")] // O→0, I y L→1, como el alfabeto de Crockford
    public void Normalize_IgnoresCaseAndHyphens_AndResolvesTheUsualConfusions(string input, string expected)
    {
        Assert.Equal(expected, RecoveryCodes.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABCD-EFGH-JKMN-PQR")] // 15
    [InlineData("ABCD-EFGH-JKMN-PQRST")] // 17
    [InlineData("ABCD-EFGH-JKMN-PQRU")] // U no existe
    [InlineData("ABCD EFGH JKMN PQRS")] // espacios en vez de guiones
    [InlineData("ABCD-EFGH-JKMN-PQR!")]
    [InlineData("ÁBCD-EFGH-JKMN-PQRS")]
    [InlineData("----------------------------------------")] // muchos guiones: demasiado largo, ni se recorre
    public void Normalize_ATextThatIsNotACode_IsNull(string? input)
    {
        Assert.Null(RecoveryCodes.Normalize(input));
    }

    [Fact]
    public void Hash_IsTheBase64Sha256OfTheNormalizedCode_AndNotTheCode()
    {
        var normalized = RecoveryCodes.Normalize(RecoveryCodes.Generate())!;
        var hash = RecoveryCodes.Hash(normalized);

        Assert.Equal(44, hash.Length);
        Assert.Equal(hash, RecoveryCodes.Hash(normalized)); // determinista: hace falta para buscarlo por el índice
        Assert.NotEqual(normalized, hash);
        Assert.DoesNotContain(normalized, hash);
        // Valor conocido (SHA-256 de "ABCDEFGHJKMNPQRS" en base64), calculado aparte con OpenSSL, no con este código.
        Assert.Equal("9ZgFYSf81Dh2UaSbOkI12M5Q1x9AkQaN9RURz8EPOI8=", RecoveryCodes.Hash("ABCDEFGHJKMNPQRS"));
    }

    [Fact]
    public void TwoEquivalentWaysOfTypingACode_HaveTheSameHash()
    {
        var typedByHand = RecoveryCodes.Normalize("abcd-efgh-jkmn-pqrs")!;
        var pasted = RecoveryCodes.Normalize("ABCDEFGHJKMNPQRS")!;

        Assert.Equal(RecoveryCodes.Hash(pasted), RecoveryCodes.Hash(typedByHand));
    }
}
