using FluentValidation;

namespace OfiFlow.Application.Identity.Commands.RefreshToken;

public sealed class RefreshTokenCommandValidator : AbstractValidator<RefreshTokenCommand>
{
    // Un refresh token son 64 bytes en base64 (88 caracteres); 200 deja margen sin permitir
    // hashear entradas de tamaño arbitrario.
    private const int MaxLength = 200;

    public RefreshTokenCommandValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(MaxLength);
    }
}
