using FluentValidation;
using OfiFlow.Application.Common.Validation;

namespace OfiFlow.Application.Identity.Commands.ForgotPassword;

/// <summary>
/// Solo la forma del email y su longitud. Es lo único que se comprueba <b>sin consultar nada</b>, así que un 400 por un email mal formado no revela si existe una cuenta (R1):
/// se decide con el texto recibido y nada más.
/// </summary>
public sealed class ForgotPasswordCommandValidator : AbstractValidator<ForgotPasswordCommand>
{
    public ForgotPasswordCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().ValidEmail();
    }
}
