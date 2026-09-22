using MediatR;

namespace OfiFlow.Application.Jobs.Commands.CompleteJob;

public sealed record CompleteJobCommand(Guid Id) : IRequest;
