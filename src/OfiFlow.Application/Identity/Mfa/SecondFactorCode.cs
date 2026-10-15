namespace OfiFlow.Application.Identity.Mfa;

/// <summary>Qué se acepta como "código" al desactivar el MFA o regenerar los códigos: el de la app o uno de recuperación.</summary>
public static class SecondFactorCode
{
    /// <summary>¿Tiene la forma de un código TOTP (6 dígitos) o de uno de recuperación? Se comprueba antes de procesar nada (ADR-009 R3).</summary>
    public static bool IsWellFormed(string? input) =>
        Totp.IsWellFormed(input) || RecoveryCodes.Normalize(input) is not null;
}
