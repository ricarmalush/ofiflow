namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// Comprueba una contraseña contra su hash. Existe como pieza aparte para poder <b>contar</b> cuántas
/// verificaciones se hacen (spec 010 R18/R19): el login debe hacer exactamente una, haya o no cuenta, esté o no
/// bloqueada, y la única forma de probarlo sin cronómetros es poder observar cada llamada.
/// </summary>
public interface IPasswordChecker
{
    /// <summary>
    /// Verifica la contraseña. Si <paramref name="storedHash"/> es <c>null</c> (no hay cuenta, o está bloqueada y no
    /// se debe comprobar de verdad), hace <b>el mismo trabajo</b> contra un hash ficticio y devuelve siempre <c>false</c>,
    /// para que la respuesta no tarde menos que cuando sí hay una contraseña real que comprobar.
    /// </summary>
    bool Verify(string? storedHash, string password);
}
