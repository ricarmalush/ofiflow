namespace OfiFlow.Domain.Tenancy;

/// <summary>
/// Códigos de error de las invitaciones (ADR-011). Son contrato público: no se cambian ni se reutilizan
/// con otro significado. El texto de cada uno está en Api/Resources/ErrorMessages.resx.
/// </summary>
public static class InvitationErrors
{
    /// <summary>
    /// La invitación no se puede usar. Es el <b>mismo</b> código para un token desconocido, caducado,
    /// revocado o ya usado: quien lo intenta no debe aprender cuál de los cuatro es (spec 009 R7).
    /// </summary>
    public const string Invalid = "invitation.invalid";

    /// <summary>Ya hay una invitación pendiente para ese email en la empresa (spec 009 R5).</summary>
    public const string AlreadyPending = "invitation.already_pending";

    /// <summary>La empresa ya tiene el máximo de invitaciones pendientes (spec 009 R5).</summary>
    public const string LimitReached = "invitation.limit_reached";

    /// <summary>La invitación no existe en la empresa activa (o es de otra: indistinguible a propósito).</summary>
    public const string NotFound = "invitation.not_found";
}
