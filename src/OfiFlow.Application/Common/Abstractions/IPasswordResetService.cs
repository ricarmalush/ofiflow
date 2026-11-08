namespace OfiFlow.Application.Common.Abstractions;

/// <summary>
/// Los tokens de restablecimiento de contraseña (spec 018, ADR-021): lo que Application pide sin saber dónde ni cómo se guardan. Lo implementa Infrastructure, donde un solo
/// fichero lee la tabla. Application nunca ve el hash ni la tabla: solo el token en claro, <b>una vez</b>, para meterlo en el correo.
/// </summary>
public interface IPasswordResetService
{
    /// <summary>
    /// Pide un token para la cuenta con este email, <b>sin revelar nada</b> a quien llama por HTTP (R1): el caso de uso responde igual con cualquiera de los tres resultados.
    /// Si la cuenta existe y no se pasó del límite (R4), anula los tokens vigentes (R3: uno solo por cuenta), crea el nuevo y lo confirma; el valor en claro solo existe en el
    /// resultado de esta llamada. Una cuenta de un operador de la plataforma cuenta como «no existe» (R14).
    /// </summary>
    Task<PasswordResetIssue> IssueAsync(string email, CancellationToken cancellationToken);

    /// <summary>
    /// Canjea el token que llegó en el enlace (R8, R10): lo busca por su hash y, si sirve (existe, no caducó, no se usó ni se anuló), lo <b>marca como usado</b> y anula los demás de la
    /// cuenta. <b>No confirma el cambio</b>: lo confirma el caso de uso en su única confirmación, junto con la contraseña nueva y las sesiones cerradas, así que si algo falla no se
    /// consume nada. Dos canjes a la vez del mismo token los resuelve la versión de fila de la tabla al confirmar: gana uno.
    /// </summary>
    Task<PasswordResetConsumption> ConsumeAsync(string token, CancellationToken cancellationToken);

    /// <summary>
    /// Anula, <b>sin confirmar</b>, los enlaces de recuperación vigentes de la cuenta (spec 019 R7, D4): quien cambia su contraseña desde dentro no debe dejar una vía para cambiarla otra vez con un enlace
    /// pedido antes (por ejemplo, por alguien con acceso al buzón). Conserva la fecha del primer uso, como al anular los demás en <see cref="ConsumeAsync"/>. La confirma el caso de uso. Solo de esa cuenta.
    /// </summary>
    Task InvalidatePendingWithoutSavingAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Qué pasó al canjear un token. Solo lo ve el caso de uso y el registro: la respuesta HTTP es el mismo error para todos los rechazos (R8).</summary>
public enum PasswordResetConsumeOutcome
{
    /// <summary>El token sirve y quedó marcado para consumir (falta confirmar). Es el único resultado con <see cref="PasswordResetConsumption.UserId"/> de una cuenta que puede cambiar.</summary>
    Consumed,

    /// <summary>No sirve: desconocido, caducado, ya usado o anulado por otro pedido, o de una cuenta que ya no existe o es de un operador de la plataforma.</summary>
    Rejected
}

/// <param name="Outcome">Qué pasó.</param>
/// <param name="UserId">La cuenta del token, si se encontró. Es un Id, no un dato personal: va al registro.</param>
/// <param name="RejectedReason">El motivo real de un rechazo (<c>Unknown</c>, <c>Expired</c>, <c>UsedOrReplaced</c>, <c>PlatformOperator</c>): solo para el registro, nunca para la respuesta.</param>
public sealed record PasswordResetConsumption(PasswordResetConsumeOutcome Outcome, Guid? UserId = null, string? RejectedReason = null);

/// <summary>Qué pasó al pedir un token. Solo lo ve el caso de uso y el registro: la respuesta HTTP es la misma para los tres.</summary>
public enum PasswordResetIssueOutcome
{
    /// <summary>Se creó un token. Es el único resultado que lleva <see cref="PasswordResetIssue.Token"/>.</summary>
    Issued,

    /// <summary>No hay cuenta con ese email, o es de un operador de la plataforma.</summary>
    UnknownAccount,

    /// <summary>La cuenta ya tiene el máximo de tokens de la última hora (R4), o hubo una carrera con un uso del token anterior: no se crea nada y no se avisa a nadie.</summary>
    Throttled
}

/// <param name="Outcome">Qué pasó.</param>
/// <param name="UserId">La cuenta, si existe (<see cref="PasswordResetIssueOutcome.Issued"/> o <see cref="PasswordResetIssueOutcome.Throttled"/>). Es un Id, no un dato personal: va al registro.</param>
/// <param name="Token">El token en claro, <b>solo</b> con <see cref="PasswordResetIssueOutcome.Issued"/>. Es un secreto: nunca al registro.</param>
public sealed record PasswordResetIssue(PasswordResetIssueOutcome Outcome, Guid? UserId = null, string? Token = null);
