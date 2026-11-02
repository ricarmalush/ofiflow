namespace OfiFlow.Domain.Identity;

/// <summary>
/// Códigos de error del contexto Identity (ADR-011). Son contrato público: no se cambian ni se
/// reutilizan con otro significado. El texto de cada uno está en Api/Resources/ErrorMessages.resx.
/// Prefijo = la entidad a la que se refiere el error (user), no el contexto (identity).
/// </summary>
public static class IdentityErrors
{
    public const string UserNameRequired = "user.name_required";

    public const string EmailAlreadyRegistered = "user.email_already_registered";

    /// <summary>
    /// La sesión que se quiere cerrar no existe, es de otra cuenta o ya estaba cerrada (spec 016 R14). Son los tres casos a propósito: la respuesta
    /// es idéntica para que la ruta no sirva de oráculo («¿existe esta sesión de otra persona?»).
    /// </summary>
    public const string SessionNotFound = "session.not_found";

    /// <summary>
    /// El enlace para restablecer la contraseña no se puede usar (spec 018 R8). Es el <b>mismo</b> código para un token desconocido, caducado, ya usado o
    /// anulado por otro pedido: quien lo intenta no debe aprender cuál de los cuatro es. El motivo real solo va al registro.
    /// </summary>
    public const string PasswordResetInvalid = "password_reset.invalid";
}
