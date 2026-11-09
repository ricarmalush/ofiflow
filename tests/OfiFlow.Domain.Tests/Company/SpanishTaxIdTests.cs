using System.Text.Json;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Company;

namespace OfiFlow.Domain.Tests.Company;

/// <summary>
/// El NIF, NIE y CIF (spec 024 R3) contra una <b>tabla de casos fijos</b> escrita aparte del código (<c>TaxIdVectors.json</c>): identificadores reales y públicos de
/// grandes empresas como ancla (si el algoritmo da su control, es el oficial), uno por cada tipo de entidad y por cada causa de invalidez. El frontend copia ese mismo
/// fichero para su ayuda al teclear: si los dos lados dan lo mismo para cada caso, la pantalla no avisa de un error que la API no daría.
/// </summary>
public class SpanishTaxIdTests
{
    private sealed record Vector(string Name, string Input, bool Valid, string? Normalized);

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static List<Vector> LoadVectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Company", "TaxIdVectors.json");
        return JsonSerializer.Deserialize<List<Vector>>(File.ReadAllText(path), Options)!;
    }

    public static TheoryData<string> VectorNames()
    {
        var data = new TheoryData<string>();
        foreach (var vector in LoadVectors())
        {
            data.Add(vector.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void EveryFixedCase_IsAcceptedOrRejected_AsTheTableSays(string name)
    {
        var vector = LoadVectors().Single(v => v.Name == name);

        Assert.Equal(vector.Valid, SpanishTaxId.IsValid(vector.Input));

        if (vector.Valid)
        {
            Assert.Equal(vector.Normalized, SpanishTaxId.Create(vector.Input).Value);
        }
        else
        {
            var error = Assert.Throws<DomainException>(() => SpanishTaxId.Create(vector.Input));
            Assert.Equal(CompanyErrors.TaxIdInvalid, error.Code);
        }
    }

    [Fact]
    public void TheFixedCases_CoverTheRulesTheyExist_ToProtect()
    {
        // Un centinela: si alguien vacía la tabla o borra los casos difíciles, los tests de arriba pasarían en vacío.
        var vectors = LoadVectors();

        Assert.True(vectors.Count >= 80);
        Assert.True(vectors.Count(v => v.Valid) >= 40);
        Assert.True(vectors.Count(v => !v.Valid) >= 30);

        // Los tres tipos, en las dos direcciones.
        foreach (var kind in new[] { "NIF", "NIE", "CIF" })
        {
            Assert.Contains(vectors, v => v.Valid && v.Name.StartsWith(kind, StringComparison.Ordinal));
            Assert.Contains(vectors, v => !v.Valid && v.Name.StartsWith($"{kind} inválido", StringComparison.Ordinal));
        }

        // Las entidades de cada clase de control: solo letra, solo dígito y cualquiera de las dos.
        foreach (var entity in "ABEHPQRSNW".Concat("CDFGJUV"))
        {
            Assert.Contains(vectors, v => v.Valid && v.Input.TrimStart().StartsWith(entity));
        }

        // Los extremos de la tabla de letras del CIF (control 0 → J, control 9 → I).
        Assert.Contains(vectors, v => v.Valid && v.Input == "G1021426J");
        Assert.Contains(vectors, v => v.Valid && v.Input == "G1039281I");

        // Los dos casos de longitud rodean el límite exacto.
        Assert.Equal(SpanishTaxId.InputMaxLength, vectors.Single(v => v.Name.Contains("justo veinte")).Input.Length);
        Assert.Equal(SpanishTaxId.InputMaxLength + 1, vectors.Single(v => v.Name.Contains("veintiún")).Input.Length);
    }

    [Fact]
    public void TheControlLetter_IsRight_ForEveryOneOfTheTwentyThreeRemainders_AndWrongForTheNextOne()
    {
        // La tabla oficial escrita aparte del código: cada resto de dividir entre 23 (0 a 22) elige su letra. Un NIF cuyo número vale justo r tiene resto r.
        const string letters = "TRWAGMYFPDXBNJZSQVHLCKE";
        Assert.Equal(23, letters.Length);

        for (var remainder = 0; remainder < letters.Length; remainder++)
        {
            var right = letters[remainder];
            var wrong = letters[(remainder + 1) % letters.Length];

            Assert.True(SpanishTaxId.IsValid($"{remainder:D8}{right}"), $"NIF con resto {remainder} y letra {right}");
            Assert.False(SpanishTaxId.IsValid($"{remainder:D8}{wrong}"), $"NIF con resto {remainder} y letra {wrong}");
            Assert.True(SpanishTaxId.IsValid($"X{remainder:D7}{right}"), $"NIE con resto {remainder} y letra {right}");
            Assert.False(SpanishTaxId.IsValid($"X{remainder:D7}{wrong}"), $"NIE con resto {remainder} y letra {wrong}");
        }
    }

    [Fact]
    public void ItIsStoredNormalized_UpperCaseAndWithoutSeparators_SoTwoWaysOfTypingItAreTheSame()
    {
        Assert.Equal(SpanishTaxId.Create("12345678Z"), SpanishTaxId.Create(" 12.345.678-z "));
        Assert.Equal("12345678Z", SpanishTaxId.Create("12 345 678 z").ToString());
    }

    [Theory]
    [InlineData(" 12.345.678-z ", "12345678Z")]
    [InlineData("x-1234567-l", "X1234567L")]
    [InlineData("\t12345678\tZ\r\n", "12345678Z")]
    [InlineData("", "")]
    public void Normalize_UpperCasesAsciiLettersAndDropsSpacesHyphensAndDots(string input, string expected)
    {
        Assert.Equal(expected, SpanishTaxId.Normalize(input));
    }

    [Fact]
    public void Normalize_OnlyUpperCasesAsciiLetters_ALookalikeFromAnotherAlphabetIsNotTurnedIntoAValidLetter()
    {
        // La «ı» turca sin punto se pasa a «I» con ToUpperInvariant; aquí no: seguiría siendo «ı» y el identificador, inválido.
        Assert.Equal("G1039281ı", SpanishTaxId.Normalize("g1039281ı"));
        Assert.False(SpanishTaxId.IsValid("G1039281ı"));
    }

    [Fact]
    public void ANull_IsNotValid_AndCreateRaisesTheCodeNotAnArgumentException()
    {
        Assert.False(SpanishTaxId.IsValid(null));
        Assert.Equal(CompanyErrors.TaxIdInvalid, Assert.Throws<DomainException>(() => SpanishTaxId.Create(null)).Code);
    }

    [Fact]
    public void TheError_NeverCarriesTheValue_BecauseATaxIdIsPersonalData()
    {
        var error = Assert.Throws<DomainException>(() => SpanishTaxId.Create("12345678A"));

        Assert.Equal(CompanyErrors.TaxIdInvalid, error.Code);
        Assert.DoesNotContain("12345678", error.Message, StringComparison.Ordinal);
        Assert.Empty(error.Arguments);
    }

    [Fact]
    public void AnEnormousInput_IsRejectedByLengthBeforeAnythingIsTraversed()
    {
        var huge = new string('1', 10_000_000) + "Z";

        Assert.False(SpanishTaxId.IsValid(huge));
    }

    [Fact]
    public void TheStoredLength_IsExactlyNine_TheSizeOfEveryValidIdentifier()
    {
        Assert.Equal(9, SpanishTaxId.MaxLength);
        Assert.All(LoadVectors().Where(v => v.Valid), v => Assert.Equal(SpanishTaxId.MaxLength, v.Normalized!.Length));
    }
}
