namespace OfiFlow.Domain.Identity;

/// <summary>
/// Códigos de error del segundo factor (ADR-011, ADR-014). Son contrato público: no se cambian ni se reutilizan
/// con otro significado. El texto de cada uno está en Api/Resources/ErrorMessages.resx.
/// Los fallos del <b>login</b> en dos pasos no están aquí a propósito: devuelven un 401 sin código, igual que
/// cualquier credencial errónea, para no decir cuál de los motivos fue (spec 010 R13).
/// </summary>
public static class MfaErrors
{
    /// <summary>La cuenta ya tiene el MFA activo; para cambiarlo hay que desactivarlo primero.</summary>
    public const string AlreadyEnabled = "mfa.already_enabled";

    /// <summary>La operación necesita el MFA activo y la cuenta no lo tiene.</summary>
    public const string NotEnabled = "mfa.not_enabled";

    /// <summary>Se intentó confirmar sin haber pedido antes el secreto (o el pendiente ya no existe).</summary>
    public const string SetupRequired = "mfa.setup_required";

    /// <summary>El código de la app (o de recuperación) no es válido al activar, desactivar o regenerar.</summary>
    public const string InvalidCode = "mfa.invalid_code";

    /// <summary>La contraseña que se pide para confirmar una operación sensible no es correcta.</summary>
    public const string InvalidPassword = "mfa.invalid_password";
}
