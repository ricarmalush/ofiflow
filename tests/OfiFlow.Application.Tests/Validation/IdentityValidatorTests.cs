using FluentValidation.TestHelper;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Commands.Login;
using OfiFlow.Application.Identity.Commands.Logout;
using OfiFlow.Application.Identity.Commands.RefreshToken;

namespace OfiFlow.Application.Tests.Validation;

/// <summary>
/// ADR-009 R3: la contraseña tiene máximo para no ejecutar PBKDF2 sobre entradas de tamaño
/// arbitrario. (Las reglas del alta de una cuenta viven con sus casos de uso: pedir el registro y
/// completarlo, spec 021.)
/// </summary>
public class IdentityValidatorTests
{
    private readonly LoginCommandValidator _login = new();
    private readonly RefreshTokenCommandValidator _refresh = new();

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

    // Spec 013: el cierre de sesión acepta exactamente la misma forma que la renovación.
    private readonly LogoutCommandValidator _logout = new();

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("a", true)]
    public void Logout_RefreshToken_MustNotBeEmpty(string? token, bool valid)
    {
        var result = _logout.TestValidate(new LogoutCommand(token!));

        if (valid)
        {
            result.ShouldNotHaveValidationErrorFor(x => x.RefreshToken);
        }
        else
        {
            result.ShouldHaveValidationErrorFor(x => x.RefreshToken);
        }
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void Logout_RefreshToken_IsLimitedTo200Characters(int length, bool valid)
    {
        var result = _logout.TestValidate(new LogoutCommand(new string('a', length)));

        if (valid)
        {
            result.ShouldNotHaveValidationErrorFor(x => x.RefreshToken);
        }
        else
        {
            result.ShouldHaveValidationErrorFor(x => x.RefreshToken);
        }
    }
}
