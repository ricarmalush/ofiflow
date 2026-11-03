using MediatR;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Domain.Common;

namespace OfiFlow.Application.Identity.Commands.ForgotPassword;

/// <summary>
/// «He olvidado mi contraseña» (spec 018, ADR-021). Lo que importa de este handler es lo que <b>no</b> hace:
/// <list type="bullet">
/// <item><b>No cambia la respuesta</b> según lo que encuentre: vuelve igual con una cuenta, sin cuenta, con el límite superado o con el correo apagado (R1). Quien lo llama es anónimo.</item>
/// <item><b>No envía el correo</b> dentro de la petición (R2, D3): lo deja en la cola y sigue. Enviarlo tardaría segundos solo cuando la cuenta existe, y ese tiempo la delataría.</item>
/// <item><b>No registra</b> el email, el token ni el enlace (ADR-009 R5): solo el Id de la cuenta, que no es un dato personal.</item>
/// </list>
/// </summary>
public sealed partial class ForgotPasswordCommandHandler(
    IPasswordResetService passwordResets,
    IEmailSender emailSender,
    IEmailQueue emailQueue,
    IEmailLinks emailLinks,
    ILogger<ForgotPasswordCommandHandler> logger)
    : IRequestHandler<ForgotPasswordCommand>
{
    public async Task Handle(ForgotPasswordCommand request, CancellationToken cancellationToken)
    {
        // R6: sin correo no hay cómo entregar el token, así que ni se crea. La petición responde igual que siempre; solo queda un aviso para quien despliega.
        // Se comprueba ANTES de pedir el token: una fila que nadie va a poder usar no debe existir.
        if (!emailSender.IsEnabled)
        {
            LogEmailOff(logger);
            return;
        }

        var issue = await passwordResets.IssueAsync(request.Email, cancellationToken);

        switch (issue.Outcome)
        {
            case PasswordResetIssueOutcome.UnknownAccount:
                LogRequested(logger, accountExists: false, userId: null);
                return;

            case PasswordResetIssueOutcome.Throttled:
                // R4: en silencio hacia fuera (la respuesta es la misma) y con un evento hacia dentro: una ráfaga sobre una cuenta es alguien intentando inundar un buzón.
                LogThrottled(logger, issue.UserId);
                return;

            case PasswordResetIssueOutcome.Issued:
                var message = PasswordResetEmail.Build(Email.Create(request.Email), emailLinks.ResetPassword(issue.Token!));

                // R2 y R15: se encola y se sigue. El resultado se ignora a propósito: si la cola está llena, ya lo registró ella y la respuesta no debe delatarlo.
                _ = emailQueue.TryEnqueue(message);
                LogRequested(logger, accountExists: true, userId: issue.UserId);
                return;

            default:
                throw new InvalidOperationException($"Resultado de restablecimiento desconocido: {issue.Outcome}.");
        }
    }

    // Solo el Id de la cuenta y si existía: nunca el email, el token ni el enlace. «Existía» solo va al registro (para un analista), jamás a la respuesta.
    [LoggerMessage(EventId = SecurityEventIds.PasswordResetRequested, Level = LogLevel.Information,
        Message = "Se pidió restablecer una contraseña. La cuenta existe: {AccountExists}, UserId {UserId}")]
    private static partial void LogRequested(ILogger logger, bool accountExists, Guid? userId);

    [LoggerMessage(EventId = SecurityEventIds.PasswordResetThrottled, Level = LogLevel.Warning,
        Message = "Se pidió restablecer la contraseña de una cuenta que ya llegó al límite de la hora. No se creó token ni se envió correo. UserId {UserId}")]
    private static partial void LogThrottled(ILogger logger, Guid? userId);

    // Sin EventId de seguridad: es configuración, no un hecho sobre una cuenta.
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Se pidió restablecer una contraseña pero el correo no está configurado (Email:Provider = None): no se crea token ni se envía nada.")]
    private static partial void LogEmailOff(ILogger logger);
}
