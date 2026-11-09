using FluentValidation;

namespace OfiFlow.Application.Identity.Commands.ChangePassword;

public sealed class ChangePasswordCommandValidator : AbstractValidator<ChangePasswordCommand>
{
    // El mismo tope que el segundo factor para el código (de la app o de recuperación).
    private const int CodeMaxLength = 64;

    public ChangePasswordCommandValidator()
    {
        // R4: las longitudes se comprueban ANTES de gastar ninguna comprobación de la contraseña actual (un error de campo no cuesta un intento del bloqueo) y antes de hashear nada: el tope de la
        // actual y de la nueva evita ejecutar PBKDF2 sobre entradas enormes. La actual solo exige que no esté vacía y no sea enorme: si es corta o rara, simplemente no será la suya.
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(PasswordRules.MaxLength);

        // Las mismas reglas del registro y del restablecimiento: de 8 a 128, sin más exigencias (NIST SP 800-63B).
        RuleFor(x => x.NewPassword).NotEmpty().Length(PasswordRules.MinLength, PasswordRules.MaxLength);

        RuleFor(x => x.Code).MaximumLength(CodeMaxLength);
    }
}
