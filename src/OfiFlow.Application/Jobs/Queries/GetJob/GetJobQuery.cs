using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Jobs.Queries.GetJob;

[RequiresPermission(Permission.JobsRead)]
public sealed record GetJobQuery(Guid Id) : IRequest<JobDto?>;
