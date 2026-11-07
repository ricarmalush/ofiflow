namespace OfiFlow.Application.Identity;

/// <summary>
/// Quién es la persona que pregunta, en qué empresa está y qué puede hacer (<c>GET /account/me</c>, spec 012). Habla <b>solo de
/// quien lo pide</b>: nada de otra persona ni de otra empresa, y ningún secreto.
/// </summary>
/// <param name="UserId">La cuenta (es el <c>sub</c> del token).</param>
/// <param name="MembershipId">
/// La pertenencia a la empresa (<c>TenantUser.Id</c>): el Id que se usa para asignar trabajos y el que lleva
/// <c>Job.AssignedTenantUserId</c>. Con él, alguien sin <c>Users.Read</c> sabe cuáles son «sus» trabajos.
/// </param>
/// <param name="Role">El rol <b>efectivo</b>: el de la base de datos en esta petición, no el que dijo el token al emitirse.</param>
/// <param name="Permissions">
/// Los permisos de ese rol, con su nombre estable (<c>Customers.Read</c>), ordenados. <b>Informativos</b>: sirven para pintar la
/// interfaz; quien decide qué se permite es siempre la API en cada operación.
/// </param>
/// <param name="SecondFactorEnrollmentRequired">
/// Spec 022: su rol efectivo exige el segundo factor y la cuenta todavía no lo tiene activo (uno pedido y sin confirmar no cuenta). Es <b>el mismo dato que usa la puerta de la API</b>,
/// leído de <c>ICurrentUser</c> (sin una consulta más): con él la interfaz lleva a la pantalla de activarlo en vez de dejar que cada pantalla choque con un 403. <b>Informativo</b>:
/// quien lo impone es siempre la API, en cada petición.
/// </param>
/// <param name="SecondFactorRequired">
/// Spec 022: su rol efectivo exige el segundo factor, <b>lo tenga activo la cuenta o no</b>. Con él la interfaz sabe que ese rol no puede desactivarlo (y esconde el botón) sin copiar la regla
/// por rol. Es la misma regla que impone la API (<c>mfa.required_for_role</c>); aquí es <b>informativo</b>.
/// </param>
public sealed record CurrentAccountDto(
    Guid UserId,
    Guid MembershipId,
    string Name,
    string Email,
    Guid TenantId,
    string TenantName,
    string Role,
    IReadOnlyList<string> Permissions,
    bool SecondFactorEnrollmentRequired,
    bool SecondFactorRequired);
