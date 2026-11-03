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
}

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
