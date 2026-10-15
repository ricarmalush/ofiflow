namespace OfiFlow.Application.Identity;

/// <summary>
/// Lo que devuelve el primer paso del login (spec 010 R10). Tiene tres casos explícitos en vez de un <c>null</c> ambiguo:
/// una sesión, un desafío (falta el segundo factor) o un rechazo. Quien lo use no puede olvidarse de tratar uno.
/// </summary>
public abstract record LoginResult
{
    private LoginResult()
    {
    }

    /// <summary>Contraseña correcta en una cuenta sin segundo factor: sesión completa.</summary>
    public sealed record Authenticated(AuthResultDto Tokens) : LoginResult;

    /// <summary>Contraseña correcta en una cuenta con segundo factor: <b>no hay sesión</b>, hay un desafío que canjear con un código.</summary>
    public sealed record SecondFactorRequired(MfaChallengeDto Challenge) : LoginResult;

    /// <summary>Email desconocido, contraseña errónea, cuenta bloqueada o sin empresa: la API responde igual (401) en todos.</summary>
    public sealed record Rejected : LoginResult;
}

/// <summary>
/// El desafío del login en dos pasos. <see cref="Challenge"/> es un valor opaco y aleatorio (no un JWT): no se puede
/// presentar como token de acceso. Caduca en <see cref="ExpiresAt"/> y se canjea una sola vez con un código.
/// </summary>
public sealed record MfaChallengeDto(string Challenge, DateTime ExpiresAt);
