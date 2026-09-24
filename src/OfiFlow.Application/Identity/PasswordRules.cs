namespace OfiFlow.Application.Identity;

/// <summary>
/// NIST SP 800-63B: longitud antes que complejidad. El máximo evita hashear con PBKDF2
/// contraseñas de tamaño arbitrario, que serían un DoS barato (ADR-009 R3).
/// </summary>
public static class PasswordRules
{
    public const int MinLength = 8;

    public const int MaxLength = 128;
}
