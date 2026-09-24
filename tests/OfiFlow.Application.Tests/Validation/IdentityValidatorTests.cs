using FluentValidation.TestHelper;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Commands.Login;
using OfiFlow.Application.Identity.Commands.RefreshToken;
using OfiFlow.Application.Identity.Commands.Register;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Validation;

/// <summary>
/// ADR-009 R3: la contraseña tiene máximo para no ejecutar PBKDF2 sobre entradas de tamaño
/// arbitrario, y los campos de registro respetan las longitudes de Domain.
/// </summary>
public class IdentityValidatorTests
{
    private readonly RegisterCommandValidator _register = new();
    private readonly LoginCommandValidator _login = new();
    private readonly RefreshTokenCommandValidator _refresh = new();

    private static RegisterCommand ValidRegister() =>
        new("Fontanería Pérez", "Juan Pérez", "juan@example.com", "contraseña-segura");

    [Theory]
    [InlineData(PasswordRules.MinLength - 1, false)]
    [InlineData(PasswordRules.MinLength, true)]
    [InlineData(PasswordRules.MaxLength, true)]
    [InlineData(PasswordRules.MaxLength + 1, false)]
    public void Register_Password_RespectsMinAndMaxLength(int length, bool isValid)
    {
        var result = _register.TestValidate(ValidRegister() with { Password = new string('a', length) });

        if (isValid)
        {
            result.ShouldNotHaveValidationErrorFor(x => x.Password);
        }
        else
        {
            result.ShouldHaveValidationErrorFor(x => x.Password);
        }
    }

    [Fact]
    public void Register_NamesAndEmail_RespectDomainLimits()
    {
        var command = new RegisterCommand(
            new string('a', Tenant.NameMaxLength + 1),
            new string('a', User.NameMaxLength + 1),
            "a@b",
            "contraseña-segura");

        var result = _register.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.CompanyName);
        result.ShouldHaveValidationErrorFor(x => x.UserName);
        result.ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void Login_PasswordLongerThanMax_IsInvalid()
    {
        _login.TestValidate(new LoginCommand("juan@example.com", new string('a', PasswordRules.MaxLength + 1)))
            .ShouldHaveValidationErrorFor(x => x.Password);
    }

    [Fact]
    public void Login_ShortPassword_IsNotRejectedByTheValidator()
    {
        // El login no impone mínimo: simplemente no coincidirá con el hash.
        _login.TestValidate(new LoginCommand("juan@example.com", "abc"))
            .ShouldNotHaveValidationErrorFor(x => x.Password);
    }

    [Fact]
    public void RefreshToken_LongerThan200_IsInvalid()
    {
        _refresh.TestValidate(new RefreshTokenCommand(new string('a', 201)))
            .ShouldHaveValidationErrorFor(x => x.RefreshToken);
    }
}
