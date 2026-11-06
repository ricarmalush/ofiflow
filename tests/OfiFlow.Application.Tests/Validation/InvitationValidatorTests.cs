using OfiFlow.Application.Common.Security;
using FluentValidation.TestHelper;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Commands.AcceptInvitation;
using OfiFlow.Application.Tenancy;
using OfiFlow.Application.Tenancy.Commands.InviteUser;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Validation;

/// <summary>ADR-009 R3: la entrada de las invitaciones se valida antes de llegar al handler.</summary>
public class InvitationValidatorTests
{
    private readonly InviteUserCommandValidator _invite = new();
    private readonly FakeLegalTerms _terms = new("2026-11");
    private readonly AcceptInvitationCommandValidator _accept;

    public InvitationValidatorTests() => _accept = new AcceptInvitationCommandValidator(_terms);

    // ===== Invitar =====

    [Fact]
    public void Invite_AValidCommand_Passes()
    {
        _invite.TestValidate(new InviteUserCommand("ana@example.com", TenantRole.Technician)).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sin-arroba")]
    [InlineData("a@b")]
    public void Invite_AnInvalidEmail_IsRejected(string email)
    {
        _invite.TestValidate(new InviteUserCommand(email, TenantRole.Technician)).ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void Invite_AnEmailLongerThanTheMaximum_IsRejected()
    {
        var email = new string('a', Email.MaxLength) + "@example.com";

        _invite.TestValidate(new InviteUserCommand(email, TenantRole.Technician)).ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(-1)]
    public void Invite_ARoleThatDoesNotExist_IsRejected(int role)
    {
        _invite.TestValidate(new InviteUserCommand("ana@example.com", (TenantRole)role)).ShouldHaveValidationErrorFor(x => x.Role);
    }

    // ===== Aceptar =====

    private static AcceptInvitationCommand ValidAccept() =>
        new(OpaqueToken.Generate().Value, "Ana Pérez", "contraseña-segura", "2026-11");

    [Fact]
    public void Accept_AValidCommand_Passes()
    {
        _accept.TestValidate(ValidAccept()).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData("corto")]
    [InlineData("esto-no-tiene-la-longitud-de-un-token-real")]
    public void Accept_AMalformedToken_IsRejected_WithTheSameCodeAsAnyOtherTokenFailure(string token)
    {
        var result = _accept.TestValidate(ValidAccept() with { Token = token });

        result.ShouldHaveValidationErrorFor(x => x.Token).WithErrorCode(InvitationErrors.Invalid);
    }

    [Fact]
    public void Accept_AnEnormousToken_IsRejectedWithoutBeingProcessed()
    {
        var result = _accept.TestValidate(ValidAccept() with { Token = new string('A', 1_000_000) });

        result.ShouldHaveValidationErrorFor(x => x.Token);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(User.NameMaxLength, true)]
    [InlineData(User.NameMaxLength + 1, false)]
    public void Accept_TheName_RespectsTheDomainLength(int length, bool isValid)
    {
        var result = _accept.TestValidate(ValidAccept() with { Name = new string('a', length) });

        if (isValid)
        {
            result.ShouldNotHaveValidationErrorFor(x => x.Name);
        }
        else
        {
            result.ShouldHaveValidationErrorFor(x => x.Name);
        }
    }

    // ===== Las condiciones de uso (spec 021 D8, R16) =====

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2026-10")]
    [InlineData("2026-11 ")]
    [InlineData(" 2026-11")]
    public void Accept_WithoutTheCurrentTermsVersion_IsAFieldError_WithTheTermsCode(string? sent)
    {
        var result = _accept.TestValidate(ValidAccept() with { AcceptedTermsVersion = sent! });

        result.ShouldHaveValidationErrorFor(x => x.AcceptedTermsVersion).WithErrorCode(IdentityErrors.TermsNotAccepted);
    }

    [Fact]
    public void Accept_TheTermsVersion_IsComparedExactly_NotIgnoringCase()
    {
        _terms.CurrentVersion = "V2026-11";

        _accept.TestValidate(ValidAccept() with { AcceptedTermsVersion = "v2026-11" }).ShouldHaveValidationErrorFor(x => x.AcceptedTermsVersion);
        _accept.TestValidate(ValidAccept() with { AcceptedTermsVersion = "V2026-11" }).ShouldNotHaveValidationErrorFor(x => x.AcceptedTermsVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Accept_WithTheTermsNotConfigured_NoVersionIsTheCurrentOne_SoNoAccountIsBornWithoutTerms(string? configured)
    {
        // Fail-closed, como el registro (R8): sin Legal:TermsVersion no nace una cuenta sin condiciones aceptadas.
        _terms.CurrentVersion = configured;

        _accept.TestValidate(ValidAccept()).ShouldHaveValidationErrorFor(x => x.AcceptedTermsVersion).WithErrorCode(IdentityErrors.TermsNotAccepted);
    }

    [Fact]
    public void Accept_ABadTermsVersion_DoesNotDependOnTheTokenBeingGood()
    {
        // Un error de CAMPO: depende solo de lo enviado. No cuenta nada del token ni de la invitación.
        var result = _accept.TestValidate(ValidAccept() with { AcceptedTermsVersion = "otra" });

        result.ShouldNotHaveValidationErrorFor(x => x.Token);
    }

    [Theory]
    [InlineData(PasswordRules.MinLength - 1, false)]
    [InlineData(PasswordRules.MinLength, true)]
    [InlineData(PasswordRules.MaxLength, true)]
    [InlineData(PasswordRules.MaxLength + 1, false)]
    public void Accept_ThePassword_RespectsTheSameMinAndMaxAsRegistration(int length, bool isValid)
    {
        // El máximo evita ejecutar PBKDF2 sobre contraseñas de tamaño arbitrario (ADR-009 R3).
        var result = _accept.TestValidate(ValidAccept() with { Password = new string('a', length) });

        if (isValid)
        {
            result.ShouldNotHaveValidationErrorFor(x => x.Password);
        }
        else
        {
            result.ShouldHaveValidationErrorFor(x => x.Password);
        }
    }
}
