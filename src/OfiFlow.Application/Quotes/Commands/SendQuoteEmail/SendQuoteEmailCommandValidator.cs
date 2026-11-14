using FluentValidation;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Application.Quotes.Commands.SendQuoteEmail;

/// <summary>
/// La forma de la petición (spec 026 R5). Se rechaza <b>antes</b> de leer nada de la base de datos o de tocar la red. El destinatario es lo único que escribe quien manda y acaba en una cabecera
/// del correo, así que la regla es más estricta que la de <see cref="Email"/> (que deja pasar, por ejemplo, una coma o unos ángulos): <b>una sola dirección, sin nada más</b>.
/// </summary>
public sealed class SendQuoteEmailCommandValidator : AbstractValidator<SendQuoteEmailCommand>
{
    public SendQuoteEmailCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();

        // Los espacios de los extremos se ignoran (los usa el handler al recortar); lo demás se comprueba tal cual.
        // El mismo código de campo que cualquier otro email (ADR-011): el mensaje lo pone la API y nunca repite el valor, que es el dato personal de un tercero (ADR-009 R5).
        RuleFor(x => x.Recipient)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(value => value.Trim().Length <= Quote.EmailRecipientMaxLength).WithErrorCode(CommonErrors.EmailInvalid)
            .Must(value => IsSingleAddress(value.Trim())).WithErrorCode(CommonErrors.EmailInvalid);
    }

    /// <summary>
    /// Una dirección sola: con la forma de un email, <b>sin</b> caracteres de control, espacios ni nada que separe o envuelva direcciones (<c>, ; &lt; &gt; "</c> y los corchetes y paréntesis de un
    /// comentario o una lista). Es la misma lista que el transporte rechaza antes de la red, más lo que no tiene sentido en una dirección de un cliente.
    /// </summary>
    public static bool IsSingleAddress(string value) =>
        Email.IsValid(value) && !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is ',' or ';' or '<' or '>' or '"' or '(' or ')' or '[' or ']' or '\\');
}
