using System.Text.RegularExpressions;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Infrastructure.Legal;

/// <summary>
/// La configuración legal (spec 021 R8, R17, D7, D10). Hoy solo una cosa: <c>Legal:TermsVersion</c>, la etiqueta de la versión vigente de las condiciones de uso (por ejemplo <c>2026-11</c>).
/// <b>Vacía, el registro está cerrado</b> (<i>fail-closed</i>): abrirlo exige configurar a la vez el correo (ADR-020) y esto. Qué dicen las condiciones y la política de privacidad lo redacta el
/// dueño del proyecto; el código solo compara la etiqueta.
/// </summary>
public sealed partial class LegalOptions
{
    public const string SectionName = "Legal";

    /// <summary>La versión vigente. Nula o vacía: el registro está cerrado.</summary>
    public string? TermsVersion { get; set; }

    /// <summary>
    /// Devuelve el motivo si la configuración no vale, o <c>null</c>. Una etiqueta corta, sin espacios ni símbolos raros: viaja en una petición, se guarda en la base de datos y se compara tal cual.
    /// Nombra el ajuste, nunca refleja el valor.
    /// </summary>
    public string? Validate()
    {
        if (string.IsNullOrEmpty(TermsVersion))
        {
            return null;
        }

        return TermsVersion.Length > User.TermsVersionMaxLength || !Label().IsMatch(TermsVersion)
            ? $"{SectionName}:TermsVersion debe ser una etiqueta de hasta {User.TermsVersionMaxLength} caracteres con solo letras, números, punto, guion y guion bajo (por ejemplo 2026-11)."
            : null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]+\z")]
    private static partial Regex Label();
}

/// <summary>Las condiciones vigentes, leídas de <see cref="LegalOptions"/>.</summary>
internal sealed class LegalTerms(Microsoft.Extensions.Options.IOptions<LegalOptions> options) : ILegalTerms
{
    public string? CurrentVersion => string.IsNullOrEmpty(options.Value.TermsVersion) ? null : options.Value.TermsVersion;

    // Una comparación exacta y sensible a mayúsculas: la etiqueta es un identificador, no un texto. Sin versión configurada, ninguna versión es la vigente (R8).
    public bool IsCurrent(string? version) => CurrentVersion is { } current && string.Equals(version, current, StringComparison.Ordinal);
}
