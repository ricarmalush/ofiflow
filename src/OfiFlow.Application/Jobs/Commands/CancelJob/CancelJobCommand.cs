using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Jobs.Commands.CancelJob;

[RequiresPermission(Permission.JobsCancel)]
public sealed record CancelJobCommand(Guid Id) : IRequest;
