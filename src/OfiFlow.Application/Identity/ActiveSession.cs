namespace OfiFlow.Application.Identity;

/// <summary>
/// Una sesión activa tal como la guarda la base de datos (spec 016), entre <c>ITokenService</c> y los casos de uso. <b>No se devuelve nunca a la API</b>:
/// lleva el navegador sin interpretar y la IP truncada; lo que sale hacia fuera es <see cref="SessionDto"/>, que convierte el navegador en una etiqueta.
/// </summary>
/// <param name="SessionId">El Id de la sesión (spec 015).</param>
/// <param name="StartedAt">Cuándo empezó la sesión.</param>
/// <param name="LastActiveAt">La última renovación: el momento en que se creó su fila activa.</param>
/// <param name="UserAgent">El navegador con el que empezó, saneado y recortado; nulo en las sesiones anteriores a la spec 016.</param>
/// <param name="IpAddress">La red desde la que empezó, ya <b>truncada</b>; nula en las sesiones anteriores a la spec 016.</param>
public sealed record ActiveSession(Guid SessionId, DateTime StartedAt, DateTime LastActiveAt, string? UserAgent, string? IpAddress);
