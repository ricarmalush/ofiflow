using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tenancy.Queries.GetUsers;

/// <summary>Quién forma parte de la empresa activa y con qué rol (spec 009).</summary>
[RequiresPermission(Permission.UsersRead)]
public sealed record GetUsersQuery : IRequest<List<TenantUserDto>>;
