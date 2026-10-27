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
public sealed record CurrentAccountDto(
    Guid UserId,
    Guid MembershipId,
    string Name,
    string Email,
    Guid TenantId,
    string TenantName,
    string Role,
    IReadOnlyList<string> Permissions);
