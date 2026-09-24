namespace OfiFlow.Domain.Identity;

/// <summary>
/// Códigos de error del contexto Identity (ADR-011). Son contrato público: no se cambian ni se
/// reutilizan con otro significado. El texto de cada uno está en Api/Resources/ErrorMessages.resx.
/// </summary>
public static class IdentityErrors
{
    public const string UserNameRequired = "user.name_required";

    public const string EmailAlreadyRegistered = "identity.email_already_registered";
}
