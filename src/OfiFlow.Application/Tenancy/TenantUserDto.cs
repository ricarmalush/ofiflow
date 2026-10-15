namespace OfiFlow.Application.Tenancy;

/// <summary>
/// Un miembro de la empresa. <paramref name="Id"/> es el del <c>TenantUser</c> (la pertenencia): es el que se usa para
/// asignarle trabajos, cambiarle el rol o quitarlo. No se expone el Id de su cuenta; <paramref name="IsCurrentUser"/>
/// dice si es quien consulta (para que no se ofrezca cambiarse el rol o quitarse a sí mismo).
/// </summary>
public sealed record TenantUserDto(Guid Id, string Name, string Email, string Role, DateTime JoinedAt, bool IsCurrentUser);
