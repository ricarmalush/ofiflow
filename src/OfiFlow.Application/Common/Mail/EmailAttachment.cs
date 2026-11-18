namespace OfiFlow.Application.Common.Mail;

/// <summary>
/// El único adjunto que un correo de OfiFlow puede llevar (spec 026, R14, D12): <b>un</b> PDF, con un nombre restringido y un tamaño máximo. Se valida <b>al construirse</b>, no en quien lo
/// usa: el puerto de correo no se fía de que quien lo llama haya comprobado nada, y un adjunto que no cumple no llega a existir.
/// </summary>
/// <remarks>
/// Por qué tan cerrado: un adjunto es una vía más de fuga y de inyección (ADR-020 lo dejó fuera). Se admite en su forma mínima: el tipo sale de una <b>lista cerrada</b> (hoy solo
/// <c>application/pdf</c>: ampliarla es una decisión, no un descuido), el nombre sale <b>solo de letras, cifras, punto, guion y guion bajo</b> (nunca una barra, unas comillas ni un salto de
/// línea que acaben en una cabecera <c>Content-Disposition</c>) y el tamaño tiene techo.
/// El contenido son los bytes de un documento con datos personales: <see cref="ToString"/> no imprime ni el nombre ni el contenido (ADR-009 R5).
/// </remarks>
public sealed class EmailAttachment
{
    /// <summary>El tamaño máximo: 5 MB. Un PDF de 100 líneas pesa del orden de 100 KB; los servidores de correo suelen aceptar entre 10 y 25 MB por mensaje.</summary>
    public const int MaxBytes = 5 * 1024 * 1024;

    public const int FileNameMaxLength = 100;

    /// <summary>La lista cerrada de tipos admitidos.</summary>
    public static readonly IReadOnlyList<string> AllowedContentTypes = ["application/pdf"];

    public EmailAttachment(string fileName, string contentType, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(contentType);
        ArgumentNullException.ThrowIfNull(content);

        if (!IsValidFileName(fileName))
        {
            throw new ArgumentException("El nombre de un adjunto solo puede llevar letras, cifras, punto, guion y guion bajo (1 a 100 caracteres), sin «..».", nameof(fileName));
        }

        if (!AllowedContentTypes.Contains(contentType, StringComparer.Ordinal))
        {
            throw new ArgumentException("El tipo del adjunto no está en la lista de tipos admitidos.", nameof(contentType));
        }

        if (content.Length is 0 or > MaxBytes)
        {
            throw new ArgumentException("Un adjunto no puede estar vacío ni pasar de 5 MB.", nameof(content));
        }

        FileName = fileName;
        ContentType = contentType;
        Content = content;
    }

    public string FileName { get; }

    public string ContentType { get; }

    public byte[] Content { get; }

    private static bool IsValidFileName(string name) =>
        name.Length is >= 1 and <= FileNameMaxLength
        && !name.Contains("..", StringComparison.Ordinal)
        && name.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' or '_');

    public override string ToString() => nameof(EmailAttachment);
}
