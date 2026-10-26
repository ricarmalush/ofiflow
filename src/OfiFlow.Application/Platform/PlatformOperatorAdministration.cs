using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Mfa;
using OfiFlow.Domain.Common;

namespace OfiFlow.Application.Platform;

public enum OperatorCreationOutcome
{
    Created,
    InvalidEmail,
    InvalidPassword,

    /// <summary>El primer código del segundo factor no era válido: no se ha guardado nada.</summary>
    InvalidCode,

    /// <summary>El email ya está ocupado, por una cuenta de empresa o por otro operador (R3): no se ha modificado nada.</summary>
    EmailAlreadyRegistered,

    /// <summary>La cuenta nueva no pudo hacerse operador (no debería pasar con una cuenta recién creada): no se ha guardado nada.</summary>
    NotCreated
}

/// <param name="UserId">La cuenta creada; solo significa algo si <paramref name="Outcome"/> es <see cref="OperatorCreationOutcome.Created"/>.</param>
/// <param name="RecoveryCodesInClear">Los códigos de recuperación en claro: es la única vez que existen fuera de su hash.</param>
public sealed record OperatorCreationResult(OperatorCreationOutcome Outcome, Guid UserId, IReadOnlyList<string> RecoveryCodesInClear)
{
    public static OperatorCreationResult Failed(OperatorCreationOutcome outcome) => new(outcome, Guid.Empty, []);
}

public enum OperatorDisableOutcome
{
    Disabled,

    /// <summary>No hay ninguna cuenta con ese email, o no es un operador, o ya estaba dado de baja: no se ha modificado nada.</summary>
    NotAnActiveOperator
}

/// <summary>
/// Alta y baja de un operador de la plataforma (spec 011 R2 y R4). A propósito <b>no es una petición de MediatR</b>: no
/// pasa por el pipeline de autorización ni se registra con <c>AddApplication</c>, y la API no la conoce. Solo la registra
/// el programa de administración del servidor (<c>OfiFlow.Admin</c>), que no es alcanzable por HTTP. Que ninguna ruta
/// de la API llegue hasta aquí lo fija un test de arquitectura.
/// </summary>
/// <remarks>
/// El alta es <b>todo o nada</b> y, sobre todo, nunca deja un operador sin segundo factor: el primer código se comprueba
/// <i>en memoria</i> (con el secreto que la persona acaba de añadir a su app) antes de tocar la base de datos; solo si vale
/// se prepara todo (cuenta, secreto ya confirmado, códigos de recuperación, operador y auditoría) y se guarda en un único
/// <c>SaveChanges</c>. Si algo falla antes, no queda ninguna fila. Cada llamada debe hacerse con su propio ámbito de
/// servicios: una que falla deja cambios sin guardar en el contexto.
/// </remarks>
public sealed partial class PlatformOperatorAdministration(
    IApplicationDbContext dbContext,
    IIdentityService identityService,
    IMfaService mfaService,
    IPlatformOperators platformOperators,
    IPlatformAudit platformAudit,
    TimeProvider timeProvider,
    ILogger<PlatformOperatorAdministration> logger)
{
    public async Task<OperatorCreationResult> CreateAsync(
        string email, string password, byte[] secret, string firstCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);

        if (secret.Length != TotpSecret.ByteLength)
        {
            throw new ArgumentException($"El secreto debe medir {TotpSecret.ByteLength} bytes.", nameof(secret));
        }

        if (!Email.IsValid(email))
        {
            return OperatorCreationResult.Failed(OperatorCreationOutcome.InvalidEmail);
        }

        if (password is null || password.Length < PasswordRules.MinLength || password.Length > PasswordRules.MaxLength)
        {
            return OperatorCreationResult.Failed(OperatorCreationOutcome.InvalidPassword);
        }

        var now = timeProvider.GetUtcNow();

        // R2: el primer código, ANTES de tocar nada. Es una función pura contra el secreto en memoria: no hay nada que
        // deshacer si falla, y es imposible llegar a guardar una cuenta de operador sin haber demostrado el segundo factor.
        var verification = Totp.Verify(secret, firstCode, now, lastAcceptedStep: 0);
        if (verification.Outcome != TotpOutcome.Valid)
        {
            return OperatorCreationResult.Failed(OperatorCreationOutcome.InvalidCode);
        }

        var userId = Guid.NewGuid();

        var identityResult = await identityService.CreateUserAsync(userId, email, password, cancellationToken);
        if (!identityResult.Succeeded)
        {
            // R3 y R1: un email ocupado (de empresa o de otro operador) no se reutiliza, y no se modifica nada.
            return OperatorCreationResult.Failed(OperatorCreationOutcome.EmailAlreadyRegistered);
        }

        if (await platformOperators.AddAsync(userId, now.UtcDateTime, cancellationToken) != PlatformOperatorCreation.Added)
        {
            return OperatorCreationResult.Failed(OperatorCreationOutcome.NotCreated);
        }

        var codes = RecoveryCodes.GenerateBatch(AuthenticationPolicy.RecoveryCodeCount);
        await mfaService.EnrollConfirmedAsync(userId, secret, verification.Step, codes.Select(code => code.Hash).ToList(), cancellationToken);

        platformAudit.Add(userId, PlatformAuditActions.OperatorCreated, tenantId: null, now.UtcDateTime);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Dos altas simultáneas con el mismo email: el índice único deja pasar a una. La otra se encuentra el email ocupado.
            return OperatorCreationResult.Failed(OperatorCreationOutcome.EmailAlreadyRegistered);
        }

        LogOperatorCreated(logger, userId);

        return new OperatorCreationResult(OperatorCreationOutcome.Created, userId, codes.Select(code => code.Code).ToList());
    }

    public async Task<OperatorDisableOutcome> DisableAsync(string email, CancellationToken cancellationToken)
    {
        var userId = await identityService.FindUserIdAsync(email, cancellationToken);
        if (userId is null)
        {
            return OperatorDisableOutcome.NotAnActiveOperator;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Cuenta de empresa, ya dado de baja o nunca fue operador: el mismo resultado, sin tocar nada.
        if (!await platformOperators.DisableAsync(userId.Value, now, cancellationToken))
        {
            return OperatorDisableOutcome.NotAnActiveOperator;
        }

        // R4 y R17: la baja y su fila de auditoría confirman juntas. La baja surte efecto en la siguiente petición del
        // operador (R10), no a los 15 minutos de su sesión.
        platformAudit.Add(userId.Value, PlatformAuditActions.OperatorDisabled, tenantId: null, now);
        await dbContext.SaveChangesAsync(cancellationToken);

        LogOperatorDisabled(logger, userId.Value);

        return OperatorDisableOutcome.Disabled;
    }

    [LoggerMessage(EventId = SecurityEventIds.PlatformOperatorCreated, Level = LogLevel.Warning,
        Message = "Operador de la plataforma creado con el comando del servidor. UserId {UserId}")]
    private static partial void LogOperatorCreated(ILogger logger, Guid userId);

    [LoggerMessage(EventId = SecurityEventIds.PlatformOperatorDisabled, Level = LogLevel.Warning,
        Message = "Operador de la plataforma dado de baja con el comando del servidor. UserId {UserId}")]
    private static partial void LogOperatorDisabled(ILogger logger, Guid userId);
}
