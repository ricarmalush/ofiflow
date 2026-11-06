using MediatR;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Domain.Common;

namespace OfiFlow.Application.Identity.Commands.RequestRegistration;

/// <summary>
/// Pedir crear una cuenta (spec 021, ADR-023). Como el de «he olvidado mi contraseña», lo que importa de este handler es lo que <b>no</b> hace:
/// <list type="bullet">
/// <item><b>No crea ninguna cuenta</b> (D1): a lo sumo un pendiente de confirmar. Quien escribe el email de otra persona no consigue nada, porque sin acceso a su buzón no hay enlace que abrir.</item>
/// <item><b>No cambia la respuesta</b> según lo que encuentre: vuelve igual con un email nuevo, con uno que ya tiene cuenta, con el límite superado o con el registro cerrado (R1).</item>
/// <item><b>No envía el correo</b> dentro de la petición (D2): lo deja en la cola y sigue. Enviarlo tardaría segundos solo cuando hay algo que enviar, y ese tiempo lo delataría.</item>
/// <item><b>No registra</b> el email, los nombres, el token ni el enlace (ADR-009 R5, R20): ni siquiera el Id de ninguna cuenta, porque aquí no hay ninguna.</item>
/// </list>
/// </summary>
public sealed partial class RequestRegistrationCommandHandler(
    IPendingRegistrationService pendingRegistrations,
    ILegalTerms legalTerms,
    IRegistrationNoticeThrottle noticeThrottle,
    IEmailSender emailSender,
    IEmailQueue emailQueue,
    IEmailLinks emailLinks,
    ILogger<RequestRegistrationCommandHandler> logger)
    : IRequestHandler<RequestRegistrationCommand>
{
    public async Task Handle(RequestRegistrationCommand request, CancellationToken cancellationToken)
    {
        // R8: sin correo no hay cómo entregar el enlace, y sin versión de las condiciones no hay unas que aceptar. El registro está CERRADO (fail-closed): no se crea nada y la respuesta es la de
        // siempre. Se comprueba ANTES de pedir el token: una fila que nadie va a poder usar no debe existir. Solo queda un aviso para quien despliega, y dice cuál de las dos cosas falta.
        if (!emailSender.IsEnabled)
        {
            LogClosedEmailOff(logger);
            return;
        }

        if (legalTerms.CurrentVersion is null)
        {
            LogClosedNoTerms(logger);
            return;
        }

        var issue = await pendingRegistrations.IssueAsync(request.Email, request.CompanyName, request.UserName, request.AcceptedTermsVersion, cancellationToken);

        switch (issue.Outcome)
        {
            case PendingRegistrationIssueOutcome.Issued:
                // R2 y R15: se encola y se sigue. El resultado se ignora a propósito: si la cola está llena, ya lo registró ella y la respuesta no debe delatarlo.
                _ = emailQueue.TryEnqueue(RegistrationEmail.Build(Email.Create(request.Email), emailLinks.CompleteRegistration(issue.Token!)));
                LogRequested(logger, accountExists: false);
                return;

            case PendingRegistrationIssueOutcome.AccountExists:
                // R6: no se crea nada y se avisa a la dirección, con un enlace SIN token. Ese aviso es un correo más hacia un buzón ajeno, así que tiene el mismo tope por email que los pendientes (R7):
                // en memoria, porque aquí no hay ninguna fila que contar. Lo que sobra no se envía y no se nota desde fuera.
                if (noticeThrottle.TryAcquire(request.Email))
                {
                    _ = emailQueue.TryEnqueue(AccountExistsEmail.Build(Email.Create(request.Email), emailLinks.ForgotPassword()));
                    LogRequested(logger, accountExists: true);
                }
                else
                {
                    LogThrottled(logger);
                }

                return;

            case PendingRegistrationIssueOutcome.Throttled:
                // R7: en silencio hacia fuera (la respuesta es la misma) y con un evento hacia dentro: una ráfaga sobre un email es alguien intentando inundar un buzón.
                LogThrottled(logger);
                return;

            default:
                throw new InvalidOperationException($"Resultado de petición de registro desconocido: {issue.Outcome}.");
        }
    }

    // «Ya tenía cuenta» solo va al registro (para un analista), jamás a la respuesta. Ni el email, ni los nombres, ni el token, ni el enlace.
    [LoggerMessage(EventId = SecurityEventIds.RegistrationRequested, Level = LogLevel.Information,
        Message = "Se pidió crear una cuenta. El email ya tenía cuenta: {AccountExists}")]
    private static partial void LogRequested(ILogger logger, bool accountExists);

    [LoggerMessage(EventId = SecurityEventIds.RegistrationThrottled, Level = LogLevel.Warning,
        Message = "Se pidió crear una cuenta para un email que ya llegó al límite de la hora. No se creó pendiente ni se envió correo.")]
    private static partial void LogThrottled(ILogger logger);

    // Sin EventId de seguridad: es configuración, no un hecho sobre una persona.
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Se pidió crear una cuenta pero el correo no está configurado (Email:Provider = None): el registro está cerrado, no se crea nada ni se envía nada.")]
    private static partial void LogClosedEmailOff(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Se pidió crear una cuenta pero las condiciones de uso no están configuradas (Legal:TermsVersion vacía): el registro está cerrado, no se crea nada ni se envía nada.")]
    private static partial void LogClosedNoTerms(ILogger logger);
}
