namespace OfiFlow.Application.Identity;

/// <summary>
/// Una de mis sesiones activas, como la ve su dueño (spec 016 R11). Lo que <b>no</b> lleva es tan importante como lo que lleva: ni el refresh token, ni su
/// hash, ni el <c>User-Agent</c> crudo (solo una etiqueta como «Chrome en Windows»), ni la IP completa (solo la truncada que se guardó).
/// </summary>
/// <param name="Id">El Id de la sesión: es lo que se pasa a <c>DELETE /account/sessions/{id}</c> para cerrarla. No es un secreto (ya viaja en el claim «sid» del token).</param>
/// <param name="Device">Una etiqueta fija («Chrome en Windows», «Safari en iPhone»…) o «Dispositivo desconocido»; nunca el texto del cliente.</param>
/// <param name="StartedAt">Cuándo empezó la sesión.</param>
/// <param name="LastActiveAt">La última renovación (como mucho cada 30 minutos de uso), no la última petición.</param>
/// <param name="ApproximateIp">La red aproximada (IP truncada) desde la que empezó, o nula si no se conoce.</param>
/// <param name="Current">Es la sesión del token de esta misma petición.</param>
public sealed record SessionDto(Guid Id, string Device, DateTime StartedAt, DateTime LastActiveAt, string? ApproximateIp, bool Current);
