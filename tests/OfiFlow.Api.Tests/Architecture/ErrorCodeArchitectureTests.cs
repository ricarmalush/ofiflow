using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using OfiFlow.Api.Common;
using OfiFlow.Api.Resources;
using OfiFlow.Domain.Common;

namespace OfiFlow.Api.Tests.Architecture;

/// <summary>
/// Guardarraíles de ADR-011 (R5): los códigos de error y el diccionario de mensajes no pueden
/// desincronizarse, y Domain no vuelve a usar excepciones genéricas para errores de negocio.
/// </summary>
public partial class ErrorCodeArchitectureTests
{
    private static readonly CultureInfo DefaultCulture = new(Localization.DefaultCulture);

    /// <summary>Lee el mismo recurso incrustado que usa la API en ejecución.</summary>
    private static readonly ResourceManager Dictionary =
        new(typeof(ErrorMessages).FullName!, typeof(ErrorMessages).Assembly);

    [Fact]
    public void EveryErrorCode_HasAMessageInTheDictionary()
    {
        var missing = AllCodesAndKeys()
            .Where(code => string.IsNullOrWhiteSpace(Dictionary.GetString(code, DefaultCulture)))
            .ToList();

        Assert.True(missing.Count == 0,
            "Estos códigos no tienen mensaje en Resources/ErrorMessages.resx, así que el usuario vería " +
            $"el código en bruto (ADR-011 R5). Añádelos al diccionario: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryDictionaryEntry_BelongsToAKnownCode()
    {
        var known = AllCodesAndKeys().ToHashSet();
        var orphans = DictionaryKeys().Where(key => !known.Contains(key)).ToList();

        Assert.True(orphans.Count == 0,
            "Estas entradas del diccionario no corresponden a ningún código (texto muerto o código " +
            $"renombrado sin actualizar el diccionario): {string.Join(", ", orphans)}");
    }

    [Fact]
    public void EveryErrorCode_FollowsTheNamingConvention()
    {
        var invalid = AllErrorCodes().Where(code => !CodeFormat().IsMatch(code)).ToList();

        Assert.True(invalid.Count == 0,
            $"Los códigos deben tener el formato entidad.error en minúsculas y snake_case (ADR-011 R1): {string.Join(", ", invalid)}");
    }

    [Theory]
    [InlineData("new ArgumentException(")]
    [InlineData("new InvalidOperationException(")]
    public void Domain_DoesNotThrowGenericExceptionsForBusinessRules(string forbidden)
    {
        var offenders = SourceCode.FilesContaining(forbidden, "OfiFlow.Domain");

        Assert.True(offenders.Count == 0,
            $"Domain no debe usar '{forbidden}' para errores de negocio: usa DomainException con un código " +
            $"(ADR-011 R1). Una excepción genérica acabaría en 500, como si fuera un bug. Aparece en: {string.Join(", ", offenders)}");
    }

    /// <summary>Códigos de error: constantes de las clases *Errors de Domain y ApiErrors de la API.</summary>
    private static IEnumerable<string> AllErrorCodes()
    {
        var domainErrorClasses = typeof(DomainException).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: true, IsSealed: true } && type.Name.EndsWith("Errors", StringComparison.Ordinal));

        return domainErrorClasses.Append(typeof(ApiErrors)).SelectMany(StringConstants);
    }

    /// <summary>Todo lo que debe estar en el diccionario: los códigos y las claves de título.</summary>
    private static IEnumerable<string> AllCodesAndKeys() =>
        AllErrorCodes().Concat(StringConstants(typeof(ProblemTitles)));

    private static IEnumerable<string> StringConstants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

    private static IEnumerable<string> DictionaryKeys() =>
        Dictionary.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true)!
            .Cast<DictionaryEntry>()
            .Select(entry => (string)entry.Key);

    [GeneratedRegex("^[a-z][a-z_]*(\\.[a-z][a-z_]*)+$")]
    private static partial Regex CodeFormat();
}
