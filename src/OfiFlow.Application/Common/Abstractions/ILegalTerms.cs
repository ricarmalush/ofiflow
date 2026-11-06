namespace OfiFlow.Application.Common.Abstractions;

/// <summary>
/// La versión vigente de las condiciones de uso (spec 021 R3, R16, R17, D7). <b>Qué dicen las condiciones y la política de privacidad no lo decide el código</b>: lo redacta el dueño del
/// proyecto. Aquí solo hay una etiqueta (<c>Legal:TermsVersion</c>, por ejemplo <c>2026-11</c>) que la API compara con la que envía quien se registra: así quien abrió un formulario antiguo no
/// acepta sin saberlo unas condiciones que ya no son las vigentes.
/// </summary>
public interface ILegalTerms
{
    /// <summary>La versión vigente, o <c>null</c> si no está configurada: entonces el registro está <b>cerrado</b> (R8, <i>fail-closed</i>).</summary>
    string? CurrentVersion { get; }

    /// <summary>¿Es la versión vigente? Falsa si no hay ninguna configurada, y falsa para una versión ausente o distinta.</summary>
    bool IsCurrent(string? version);
}
