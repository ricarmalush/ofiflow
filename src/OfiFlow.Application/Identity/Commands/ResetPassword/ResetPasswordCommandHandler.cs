using System.Diagnostics.CodeAnalysis;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Application.Identity.Commands.ResetPassword;

/// <summary>
/// Restablecer la contraseña con el token del correo (spec 018, ADR-021). Cuatro cosas cambian y las cuatro se confirman <b>juntas</b>, en un único <c>SaveChangesAsync</c> (R9): el token
/// pasa a usado (y los demás de la cuenta se anulan), la contraseña es la nueva, el contador y el bloqueo vuelven a cero y todas las sesiones se cierran. Si algo falla no cambia nada: un
/// fallo entre medias dejaría la contraseña cambiada con las sesiones vivas (justo lo que el atacante que ya tenía sesión necesita) o las sesiones cerradas con la contraseña vieja.
/// </summary>
/// <remarks>
/// <b>No toca el segundo factor</b> (R11, D7): ni lo lee ni lo cambia. Controlar el buzón basta para elegir una contraseña, no para entrar. Y <b>no desbloquea</b> nada que no sea de
/// la cuenta (R13): una empresa suspendida sigue sin entrar, porque eso lo decide el login, no la contraseña.
/// </remarks>
public sealed partial class ResetPasswordCommandHandler(
    IPasswordResetService passwordResets,
    IIdentityService identityService,
    ITokenService tokenService,
    IApplicationDbContext dbContext,
    IEmailQueue emailQueue,
    ILogger<ResetPasswordCommandHandler> logger)
    : IRequestHandler<ResetPasswordCommand>
{
    public async Task Handle(ResetPasswordCommand request, CancellationToken cancellationToken)
    {
        // Primero el token, que es barato: un token que no sirve se rechaza sin pagar el hash lento de la contraseña (este endpoint es anónimo).
        var consumption = await passwordResets.ConsumeAsync(request.Token, cancellationToken);

        if (consumption.Outcome != PasswordResetConsumeOutcome.Consumed)
        {
            // R8: el MISMO error para los cuatro motivos (y los dos de cuenta). El motivo real solo va al registro.
            Reject(consumption.RejectedReason ?? "Unknown", consumption.UserId);
        }

        var userId = consumption.UserId!.Value;

        // Sin confirmar: el cambio de contraseña y el reinicio del bloqueo se confirman con el resto.
        var email = await identityService.SetPasswordAsync(userId, request.NewPassword, cancellationToken);
        if (email is null)
        {
            // Una cuenta dada de baja entre medias: sus tokens se borran con ella, así que casi no puede ocurrir, pero la tabla no tiene clave foránea y no se supone.
            Reject("AccountGone", userId);
        }

        var closedSessions = await tokenService.RevokeAllRefreshTokensWithoutSavingAsync(userId, cancellationToken);

        try
        {
            // La única confirmación (R9). R10: si otra petición canjeó el mismo token a la vez, la versión de fila de la tabla hace fallar a esta, y con ella el cambio de contraseña
            // y el cierre de sesiones: gana una sola. Se capturan juntas la excepción de concurrencia y cualquier otra de la base de datos, como al aceptar una invitación.
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            Reject("Concurrent", userId);
        }

        // R12: el aviso. Se encola y se sigue (el mismo patrón que pedir el restablecimiento): si la cola está llena o el correo apagado, ya lo registró ella y la contraseña ya cambió.
        _ = emailQueue.TryEnqueue(PasswordChangedEmail.Build(Email.Create(email)));

        LogCompleted(logger, userId, closedSessions);
    }

    [DoesNotReturn]
    private void Reject(string reason, Guid? userId)
    {
        LogRejected(logger, reason, userId);
        throw new DomainException(IdentityErrors.PasswordResetInvalid);
    }

    // Nunca el token, la contraseña ni el email (ADR-009 R5). La IP la añade el scope de la API.
    [LoggerMessage(EventId = SecurityEventIds.PasswordResetCompleted, Level = LogLevel.Information,
        Message = "Contraseña restablecida con el token del correo. UserId {UserId}, sesiones cerradas {ClosedSessions}")]
    private static partial void LogCompleted(ILogger logger, Guid userId, int closedSessions);

    [LoggerMessage(EventId = SecurityEventIds.PasswordResetRejected, Level = LogLevel.Warning,
        Message = "Restablecimiento de contraseña rechazado. Motivo {Reason}, UserId {UserId}")]
    private static partial void LogRejected(ILogger logger, string reason, Guid? userId);
}
