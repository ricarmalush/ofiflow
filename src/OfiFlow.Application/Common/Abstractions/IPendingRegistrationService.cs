namespace OfiFlow.Application.Common.Abstractions;

/// <summary>
/// Los registros pendientes de confirmar (spec 021, ADR-023): lo que escribió alguien que quiere crear una cuenta, <b>hasta que abre el enlace de su correo</b>. No hay cuenta, ni
/// empresa, ni contraseña en ninguno. El servicio guarda <b>solo el hash</b> del token (R4) y es el único que lee su tabla (la implementación lo hace en un solo fichero, y un test de
/// arquitectura lo vigila). Como <see cref="IPasswordResetService"/>, ninguna de sus dos operaciones confirma el cambio por su cuenta si va a formar parte de una operación mayor.
/// </summary>
public interface IPendingRegistrationService
{
    /// <summary>
    /// Pide un registro para un email nuevo (R4, R5, R6, R7). Si ese email <b>ya tiene cuenta</b> (de una empresa o de un operador de la plataforma) no crea nada y lo dice; si ya llegó al límite
    /// de la hora, tampoco crea nada y lo dice. Si no, anula el pendiente anterior de ese email, crea el nuevo y devuelve el token <b>en claro, una sola vez</b>, para el correo.
    /// </summary>
    Task<PendingRegistrationIssue> IssueAsync(string email, string companyName, string userName, string termsVersion, CancellationToken cancellationToken);

    /// <summary>
    /// Canjea un token (R10, R11): lo marca como usado, <b>sin confirmar el cambio</b>: la confirmación la hace el caso de uso en su única
    /// <c>SaveChangesAsync</c>, junto con la creación de la empresa y del usuario, y es la versión de fila la que decide entre dos canjes simultáneos. Devuelve lo que la persona escribió al
    /// pedirlo. Cualquier motivo de rechazo (desconocido, canjeado o sustituido, caducado, o un email que ya tiene cuenta desde que se pidió) sale como <see cref="PendingRegistrationConsumeOutcome.Rejected"/>
    /// con su <b>motivo real</b>, que solo viaja hasta el registro.
    /// </summary>
    Task<PendingRegistrationConsumption> ConsumeAsync(string token, CancellationToken cancellationToken);
}

public enum PendingRegistrationIssueOutcome
{
    /// <summary>Se creó el pendiente: hay un token que enviar.</summary>
    Issued,

    /// <summary>El email ya tiene cuenta: no se creó nada y hay que enviar el aviso (R6).</summary>
    AccountExists,

    /// <summary>Se llegó al límite de la hora para ese email (R7): no se creó nada ni se envía nada, y a quien lo pidió no se le dice.</summary>
    Throttled
}

public sealed record PendingRegistrationIssue(PendingRegistrationIssueOutcome Outcome, string? Token = null);

public enum PendingRegistrationConsumeOutcome
{
    Consumed,

    Rejected
}

/// <param name="Outcome">Si se canjeó.</param>
/// <param name="Email">Lo que se escribió al pedirlo: solo si se canjeó.</param>
/// <param name="RejectedReason">El motivo real del rechazo (<c>Unknown</c>, <c>UsedOrReplaced</c>, <c>Expired</c>, <c>EmailTaken</c>): solo para el registro, nunca para la respuesta.</param>
public sealed record PendingRegistrationConsumption(
    PendingRegistrationConsumeOutcome Outcome,
    string? Email = null,
    string? CompanyName = null,
    string? UserName = null,
    string? TermsVersion = null,
    DateTime? TermsAcceptedAt = null,
    string? RejectedReason = null);
