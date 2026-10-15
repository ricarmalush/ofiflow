using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tenancy.Commands.ChangeUserRole;

/// <summary>Cambia el rol de un miembro de la empresa activa. <c>TenantUserId</c> es el de la pertenencia (ver <see cref="TenantUserDto"/>).</summary>
[RequiresPermission(Permission.UsersManage)]
public sealed record ChangeUserRoleCommand(Guid TenantUserId, TenantRole NewRole) : IRequest;
