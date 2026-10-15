using System.Collections.Concurrent;
using OfiFlow.Infrastructure.Identity;

namespace OfiFlow.Api.Tests.Common;

/// <summary>
/// Un verificador de contraseñas que <b>cuenta</b> cuántas veces se le llama y con qué tipo de hash (spec 010 R18/R19).
/// Delega en el verificador real: no cambia el comportamiento, solo lo observa. Es la forma de probar "se hizo
/// exactamente una verificación" sin cronómetros, que serían intermitentes.
/// </summary>
public sealed class CountingPasswordChecker : IPasswordChecker
{
    private readonly ConcurrentQueue<bool> _calls = new();

    /// <summary>Verificaciones contra un hash real (hay una contraseña de verdad que comprobar).</summary>
    public int RealVerifications => _calls.Count(isReal => isReal);

    /// <summary>Verificaciones contra el hash ficticio (no hay cuenta, o está bloqueada).</summary>
    public int DummyVerifications => _calls.Count(isReal => !isReal);

    public int TotalVerifications => _calls.Count;

    public bool Verify(string? storedHash, string password)
    {
        _calls.Enqueue(storedHash is not null);

        return PasswordHasherChecker.Instance.Verify(storedHash, password);
    }
}
