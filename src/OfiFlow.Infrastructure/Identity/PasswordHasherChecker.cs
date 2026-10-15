using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// La verificación real, con el <see cref="PasswordHasher{TUser}"/> de ASP.NET Core Identity (PBKDF2 con sal): el
/// mismo algoritmo y el mismo coste con el que se guardan las contraseñas, de modo que el hash ficticio tarda
/// <b>lo mismo</b> que uno real.
/// </summary>
public sealed class PasswordHasherChecker : IPasswordChecker
{
    public static readonly PasswordHasherChecker Instance = new();

    private static readonly PasswordHasher<ApplicationUser> Hasher = new();

    // Un hash de una contraseña que NADIE conoce: se genera al azar al arrancar y no se guarda ni se muestra. Así
    // verificar contra él no puede salir bien nunca, ni siquiera con la contraseña "más probable" del mundo. Se
    // calcula una sola vez, la primera vez que hace falta.
    private static readonly Lazy<string> DummyHash = new(() =>
        Hasher.HashPassword(new ApplicationUser(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

    private static readonly ApplicationUser NoUser = new();

    public bool Verify(string? storedHash, string password)
    {
        if (storedHash is null)
        {
            // El resultado se descarta a propósito: lo que importa es gastar el mismo tiempo.
            _ = Hasher.VerifyHashedPassword(NoUser, DummyHash.Value, password);
            return false;
        }

        return Hasher.VerifyHashedPassword(NoUser, storedHash, password) != PasswordVerificationResult.Failed;
    }
}
