using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Mail;

namespace OfiFlow.Infrastructure.Mail;

/// <summary>
/// El «transporte» cuando <c>Email:Provider</c> es <c>None</c> (spec 017, R4, D5): <b>no envía nada y falla en voz alta</b> si alguien lo llama. Los casos de uso
/// deben comprobar antes si hay correo (<see cref="IsEnabled"/> en <see cref="EmailOptions"/>); si un uso nuevo se olvida de hacerlo, esto lo hace saltar en
/// las pruebas en vez de «enviar» a ninguna parte y dar por entregado un correo que nunca existió.
/// </summary>
internal sealed class NullEmailSender : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("El correo no está configurado (Email:Provider = None): un caso de uso no debería enviar sin comprobar antes que hay correo.");
}
