using MediatR;

namespace OfiFlow.Application.Jobs.Commands.StartJob;

public sealed record StartJobCommand(Guid Id) : IRequest;
