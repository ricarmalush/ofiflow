using MediatR;

namespace OfiFlow.Application.Jobs.Commands.CancelJob;

public sealed record CancelJobCommand(Guid Id) : IRequest;
