using FluentValidation;

namespace OfiFlow.Application.Identity.Commands.Logout;

public sealed class LogoutCommandValidator : AbstractValidator<LogoutCommand>
{
    // Las mismas reglas de forma que /auth/refresh: 88 caracteres reales, 200 de margen sin hashear entradas arbitrarias.
    private const int MaxLength = 200;

    public LogoutCommandValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(MaxLength);
    }
}
