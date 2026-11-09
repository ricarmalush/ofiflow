using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Application.Identity.Commands.ChangePassword;

/// <summary>
/// Cambiar la contraseña estando dentro (spec 019, ADR-025). El orden es la seguridad (R3): <b>1)</b> la contraseña actual, con la misma función que el segundo factor (el intento se reserva antes de
/// comprobar y cuenta para el bloqueo de cuenta); <b>2)</b> el código del segundo factor, solo si la cuenta lo tiene activo (un error de la actual no gasta una comprobación del código); <b>3)</b> que la nueva
/// no sea igual a la actual (solo se aplica a quien ya demostró la actual); y <b>4)</b> el cambio, que es <b>todo o nada</b> en UN solo <c>SaveChangesAsync</c> (R5): la contraseña, las demás sesiones, los
/// enlaces de recuperación pendientes y el contador de intentos. Si algo falla no cambia nada: una contraseña cambiada con las sesiones ajenas vivas es justo lo que quien tenía una sesión robada necesita.
/// </summary>
/// <remarks>
/// <b>Se conserva la sesión actual</b> y solo ella (R6, D3). <b>No toca el segundo factor</b> (R9). El aviso por correo se encola <b>después</b> de confirmar y nunca antes (si la confirmación fallara, la persona
/// recibiría un aviso de un cambio que no ocurrió).
/// </remarks>
public sealed partial class ChangePasswordCommandHandler(
    ICurrentUser currentUser,
    IIdentityService identityService,
    IMfaService mfaService,
    ITokenService tokenService,
    IPasswordResetService passwordResets,
    IApplicationDbContext dbContext,
    IEmailQueue emailQueue,
    ILogger<ChangePasswordCommandHandler> logger)
    : IRequestHandler<ChangePasswordCommand>
{
    public async Task Handle(ChangePasswordCommand request, CancellationToken cancellationToken)
    {
        // La cuenta sale del token ya validado, nunca de la petición (R11): no hay forma de cambiar la contraseña de otra persona.
        var userId = currentUser.UserId!.Value;

        // R1. Los fallos cuentan para el bloqueo; bloqueada o errónea no se distinguen; acertar no reinicia el contador.
        if (!await identityService.ConfirmPasswordAsync(userId, request.CurrentPassword, cancellationToken))
        {
            throw new BusinessRuleException(IdentityErrors.PasswordCurrentInvalid);
        }

        // R2. Solo con el segundo factor activo; sin él, el campo `Code` se ignora. Un código ausente no es un intento: se rechaza sin gastar una comprobación.
        if ((await mfaService.GetStatusAsync(userId, cancellationToken)).IsEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.Code) || !(await mfaService.VerifySecondFactorAsync(userId, request.Code, cancellationToken)).Valid)
            {
                throw new BusinessRuleException(MfaErrors.InvalidCode);
            }
        }

        // R3/D5. Se compara en memoria, sin hashear de nuevo: las dos cadenas las puso quien ya demostró la actual.
        if (string.Equals(request.NewPassword, request.CurrentPassword, StringComparison.Ordinal))
        {
            throw new BusinessRuleException(IdentityErrors.PasswordUnchanged);
        }

        // De aquí en adelante NADA confirma por su cuenta: el cambio de contraseña (que además pone a cero el contador y el bloqueo), el cierre de las demás sesiones y la anulación de los enlaces
        // pendientes se confirman juntos, una sola vez, abajo.
        var email = await identityService.SetPasswordAsync(userId, request.NewPassword, cancellationToken);
        if (email is null)
        {
            // Una cuenta dada de baja entre medias: casi no puede ocurrir (acaba de confirmarse su contraseña) y la contraseña actual ya no vale.
            throw new BusinessRuleException(IdentityErrors.PasswordCurrentInvalid);
        }

        var closedSessions = await tokenService.RevokeOtherRefreshTokensWithoutSavingAsync(userId, currentUser.SessionId, cancellationToken);
        await passwordResets.InvalidatePendingWithoutSavingAsync(userId, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Otro cambio (u otro canje de un enlace) confirmó antes y esta petición leyó un estado que ya no existe: no se deja nada y, como la contraseña «actual» que mandó ya no vale,
            // se responde igual que con una actual errónea (nunca un 500). Un cambio simultáneo no es un ataque que merezca un evento aparte.
            throw new BusinessRuleException(IdentityErrors.PasswordCurrentInvalid);
        }

        // R8: el aviso fijo, encolado y sin esperar: con la cola llena o el correo apagado, ya lo registró ella y la contraseña ya cambió.
        _ = emailQueue.TryEnqueue(PasswordChangedEmail.Build(Email.Create(email)));

        LogChanged(logger, userId, closedSessions);
    }

    // Nunca la contraseña, el código ni el email (ADR-009 R5). La IP la añade el scope de la API.
    [LoggerMessage(EventId = SecurityEventIds.PasswordChangedFromSession, Level = LogLevel.Information,
        Message = "Contraseña cambiada desde una sesión. UserId {UserId}, sesiones ajenas cerradas {ClosedSessions}")]
    private static partial void LogChanged(ILogger logger, Guid userId, int closedSessions);
}
