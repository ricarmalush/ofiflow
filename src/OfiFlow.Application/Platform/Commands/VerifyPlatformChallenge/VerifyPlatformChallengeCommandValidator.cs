using FluentValidation;

namespace OfiFlow.Application.Platform.Commands.VerifyPlatformChallenge;

public sealed class VerifyPlatformChallengeCommandValidator : AbstractValidator<VerifyPlatformChallengeCommand>
{
    /// <summary>Un código de recuperación con guiones mide 19; cualquier texto mayor que esto no puede ser un código ni un desafío.</summary>
    private const int MaxInputLength = 64;

    public VerifyPlatformChallengeCommandValidator()
    {
        // Solo un tope de tamaño (ADR-009 R3). La FORMA exacta no se valida aquí a propósito: un desafío o un código mal formados
        // deben dar la misma respuesta (401) que uno bueno pero equivocado (R6), y un validador respondería 400.
        RuleFor(x => x.Challenge).NotEmpty().MaximumLength(MaxInputLength);
        RuleFor(x => x.Code).NotEmpty().MaximumLength(MaxInputLength);
    }
}
