using FluentValidation;

namespace OfiFlow.Application.Identity.Commands.VerifyMfaChallenge;

public sealed class VerifyMfaChallengeCommandValidator : AbstractValidator<VerifyMfaChallengeCommand>
{
    /// <summary>Un código de recuperación con guiones mide 19; cualquier texto mayor que esto no puede ser un código ni un desafío.</summary>
    private const int MaxInputLength = 64;

    public VerifyMfaChallengeCommandValidator()
    {
        // Solo un tope de tamaño (ADR-009 R3): no se hashea texto arbitrario enorme. La FORMA exacta (longitud y alfabeto) NO
        // se valida aquí a propósito: un desafío o un código mal formados deben dar la misma respuesta (401) que uno
        // bueno pero equivocado (spec 010 R13), y un validador respondería 400.
        RuleFor(x => x.Challenge).NotEmpty().MaximumLength(MaxInputLength);
        RuleFor(x => x.Code).NotEmpty().MaximumLength(MaxInputLength);
    }
}
