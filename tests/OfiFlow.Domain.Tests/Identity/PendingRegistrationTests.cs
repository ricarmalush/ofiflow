using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Domain.Tests.Identity;

/// <summary>
/// El registro pendiente (spec 021 R4, R5, R9, R11): vigencia, canje y anulación. La hora entra siempre por parámetro, así que ninguna de estas pruebas espera nada.
/// </summary>
public class PendingRegistrationTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);

    private static PendingRegistration New(DateTime? at = null, string email = "Ana.Perez@Example.com") =>
        PendingRegistration.Create(email, "Fontanería Demo SL", "Ana Pérez", "2026-11", "hash-del-token", at ?? Now);

    [Fact]
    public void ANewRegistration_KeepsWhatWasWritten_AndExpiresADayLater()
    {
        var pending = New();

        Assert.Equal("Ana.Perez@Example.com", pending.Email);
        Assert.Equal("Fontanería Demo SL", pending.CompanyName);
        Assert.Equal("Ana Pérez", pending.UserName);
        Assert.Equal("2026-11", pending.TermsVersion);
        Assert.Equal(Now, pending.TermsAcceptedAt);
        Assert.Equal("hash-del-token", pending.TokenHash);
        Assert.Equal(Now, pending.CreatedAt);
        Assert.Equal(Now.AddHours(24), pending.ExpiresAt);
        Assert.Equal(TimeSpan.FromHours(24), PendingRegistration.Lifetime);
        Assert.Null(pending.UsedAt);
        Assert.NotEqual(Guid.Empty, pending.Id);
    }

    [Fact]
    public void TheNormalizedEmail_IsTheUppercaseOne_TheRuleOfRegistrationAndLogin()
    {
        Assert.Equal("ANA.PEREZ@EXAMPLE.COM", New().NormalizedEmail);
    }

    [Fact]
    public void Create_TrimsTheNamesAndTheEmail()
    {
        var pending = PendingRegistration.Create("  ana@example.com  ", "  Mi Empresa  ", "  Ana  ", "2026-11", "hash", Now);

        Assert.Equal("ana@example.com", pending.Email);
        Assert.Equal("Mi Empresa", pending.CompanyName);
        Assert.Equal("Ana", pending.UserName);
    }

    [Fact]
    public void TheLimitPerEmail_IsThreeAnHour_TheApprovedValue()
    {
        Assert.Equal(3, PendingRegistration.MaxPerEmailPerHour);
    }

    [Fact]
    public void TwoRegistrations_NeverShareAnId()
    {
        Assert.NotEqual(New().Id, New().Id);
    }

    // ===== Vigencia =====

    [Fact]
    public void ANewRegistration_IsUsableRightAway_AndUntilTheLastInstantBeforeItExpires()
    {
        var pending = New();

        Assert.True(pending.IsUsableAt(Now));
        Assert.True(pending.IsUsableAt(Now.AddHours(23)));
        Assert.True(pending.IsUsableAt(pending.ExpiresAt.AddTicks(-1)));
    }

    [Fact]
    public void ARegistration_StopsBeingUsableAtTheExactInstantItExpires()
    {
        var pending = New();

        // El corte exacto: caducar es una fecha, no un cambio de estado. Y no vale ni a la hora de la recuperación de contraseña (1 h) ni vale 24 h después.
        Assert.False(pending.IsUsableAt(pending.ExpiresAt));
        Assert.False(pending.IsUsableAt(pending.ExpiresAt.AddMinutes(1)));
        Assert.False(pending.IsUsableAt(Now.AddDays(2)));
    }

    [Fact]
    public void TheLifetime_IsNotTheHourOfPasswordRecovery()
    {
        Assert.True(New().IsUsableAt(Now.AddHours(2)), "el enlace de crear una cuenta dura un día, no una hora");
    }

    // ===== Canje =====

    [Fact]
    public void Consuming_MarksItUsed_AndItCannotBeUsedAgain()
    {
        var pending = New();

        pending.Consume(Now.AddMinutes(5));

        Assert.Equal(Now.AddMinutes(5), pending.UsedAt);
        Assert.False(pending.IsUsableAt(Now.AddMinutes(6)));
    }

    [Fact]
    public void ConsumingTwice_Fails_WithTheSingleInvalidCode_AndKeepsTheFirstUse()
    {
        var pending = New();
        pending.Consume(Now.AddMinutes(5));

        var exception = Assert.Throws<DomainException>(() => pending.Consume(Now.AddMinutes(6)));

        Assert.Equal(IdentityErrors.RegistrationInvalid, exception.Code);
        Assert.Equal(Now.AddMinutes(5), pending.UsedAt);
    }

    [Fact]
    public void ConsumingAnExpiredRegistration_Fails_WithTheSameCode()
    {
        var pending = New();

        var exception = Assert.Throws<DomainException>(() => pending.Consume(pending.ExpiresAt));

        Assert.Equal(IdentityErrors.RegistrationInvalid, exception.Code);
        Assert.Null(pending.UsedAt);
    }

    [Fact]
    public void ConsumingAnInvalidatedRegistration_Fails_WithTheSameCode()
    {
        // Los tres motivos (canjeado, caducado, anulado) dan el MISMO código: quien lo intenta no aprende cuál fue (R11).
        var pending = New();
        pending.Invalidate(Now.AddMinutes(1));

        var exception = Assert.Throws<DomainException>(() => pending.Consume(Now.AddMinutes(2)));

        Assert.Equal(IdentityErrors.RegistrationInvalid, exception.Code);
    }

    [Fact]
    public void TheCodes_AreTheApprovedContractValues()
    {
        Assert.Equal("registration.invalid", IdentityErrors.RegistrationInvalid);
        Assert.Equal("registration.terms_not_accepted", IdentityErrors.TermsNotAccepted);
    }

    // ===== Anulación =====

    [Fact]
    public void Invalidating_MakesItUnusable()
    {
        var pending = New();

        pending.Invalidate(Now.AddMinutes(1));

        Assert.Equal(Now.AddMinutes(1), pending.UsedAt);
        Assert.False(pending.IsUsableAt(Now.AddMinutes(2)));
    }

    [Fact]
    public void Invalidating_AnAlreadyUsedRegistration_ChangesNothing_AndDoesNotFail()
    {
        var pending = New();
        pending.Consume(Now.AddMinutes(5));

        pending.Invalidate(Now.AddMinutes(30));

        Assert.Equal(Now.AddMinutes(5), pending.UsedAt); // la fecha del primer uso se conserva
    }

    [Fact]
    public void Invalidating_AnExpiredRegistration_DoesNotFail()
    {
        var pending = New();

        pending.Invalidate(Now.AddDays(3));

        Assert.False(pending.IsUsableAt(Now.AddDays(3)));
    }

    // ===== Lo que Create no admite =====

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sin-arroba")]
    [InlineData("a@b")]
    public void Create_RefusesAnInvalidEmail_WithTheCommonErrorAndNeverTheValue(string email)
    {
        var exception = Assert.ThrowsAny<DomainException>(() => PendingRegistration.Create(email, "Empresa", "Ana", "2026-11", "hash", Now));

        Assert.StartsWith("common.email_", exception.Code);
        if (email.Trim().Length > 0)
        {
            // Solo el código, nunca el valor: es un dato personal y podría acabar en un log (ADR-009 R5). Con un valor vacío la comprobación no tiene sentido: cualquier texto lo contiene.
            Assert.DoesNotContain(email.Trim(), exception.Message ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RefusesAnEmptyCompanyName(string name)
    {
        Assert.Throws<ArgumentException>(() => PendingRegistration.Create("a@example.com", name, "Ana", "2026-11", "hash", Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RefusesAnEmptyUserName(string name)
    {
        Assert.Throws<ArgumentException>(() => PendingRegistration.Create("a@example.com", "Empresa", name, "2026-11", "hash", Now));
    }

    [Fact]
    public void Create_AcceptsNamesAsLongAsTheColumns_AndRefusesOneMore()
    {
        var company = new string('e', Tenant.NameMaxLength);
        var person = new string('p', User.NameMaxLength);

        var pending = PendingRegistration.Create("a@example.com", company, person, "2026-11", "hash", Now);

        Assert.Equal(Tenant.NameMaxLength, pending.CompanyName.Length);
        Assert.Equal(User.NameMaxLength, pending.UserName.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => PendingRegistration.Create("a@example.com", company + "x", "Ana", "2026-11", "hash", Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => PendingRegistration.Create("a@example.com", "Empresa", person + "x", "2026-11", "hash", Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RefusesAnEmptyTermsVersion(string version)
    {
        Assert.Throws<ArgumentException>(() => PendingRegistration.Create("a@example.com", "Empresa", "Ana", version, "hash", Now));
    }

    [Fact]
    public void Create_AcceptsATermsVersionAsLongAsTheColumn_AndRefusesOneMore()
    {
        var version = new string('v', User.TermsVersionMaxLength);

        Assert.Equal(version, PendingRegistration.Create("a@example.com", "Empresa", "Ana", version, "hash", Now).TermsVersion);
        Assert.Throws<ArgumentOutOfRangeException>(() => PendingRegistration.Create("a@example.com", "Empresa", "Ana", version + "x", "hash", Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RefusesAnEmptyHash(string hash)
    {
        Assert.Throws<ArgumentException>(() => PendingRegistration.Create("a@example.com", "Empresa", "Ana", "2026-11", hash, Now));
    }

    [Fact]
    public void Create_AcceptsAHashAsLongAsTheColumn_AndRefusesOneMore()
    {
        Assert.Equal(PendingRegistration.TokenHashMaxLength, PendingRegistration.Create("a@example.com", "Empresa", "Ana", "2026-11", new string('a', PendingRegistration.TokenHashMaxLength), Now).TokenHash.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => PendingRegistration.Create("a@example.com", "Empresa", "Ana", "2026-11", new string('a', PendingRegistration.TokenHashMaxLength + 1), Now));
    }

    [Fact]
    public void TheTokenHashColumn_FitsASha256InBase64()
    {
        Assert.True(PendingRegistration.TokenHashMaxLength >= 44);
    }
}
