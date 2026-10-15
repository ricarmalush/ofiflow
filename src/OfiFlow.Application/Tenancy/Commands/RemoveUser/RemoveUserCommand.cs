using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tenancy.Commands.RemoveUser;

/// <summary>
/// Quita a un miembro de la empresa activa (spec 009 R14). Es una baja de verdad: se borran su pertenencia, su ficha, sus
/// credenciales y sus sesiones, y se le desasignan los trabajos sin terminar. <c>TenantUserId</c> es el de la pertenencia.
/// </summary>
[RequiresPermission(Permission.UsersManage)]
public sealed record RemoveUserCommand(Guid TenantUserId) : IRequest;
