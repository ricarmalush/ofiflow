using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;

namespace OfiFlow.Domain.Tests.Identity;

/// <summary>
/// El token de restablecimiento de contraseña (spec 018 R3, R8): vigencia, consumo y anulación. La hora entra siempre por parámetro, así que ninguna de estas
/// pruebas espera nada.
/// </summary>
public class PasswordResetTokenTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid UserId = Guid.NewGuid();

    private static PasswordResetToken New(DateTime? at = null) => PasswordResetToken.Create(UserId, "hash-del-token", at ?? Now);

    [Fact]
    public void ANewToken_BelongsToTheAccount_AndExpiresAnHourLater()
    {
        var token = New();

        Assert.Equal(UserId, token.UserId);
        Assert.Equal("hash-del-token", token.TokenHash);
        Assert.Equal(Now, token.CreatedAt);
        Assert.Equal(Now.AddHours(1), token.ExpiresAt);
        Assert.Equal(TimeSpan.FromHours(1), PasswordResetToken.Lifetime);
        Assert.Null(token.UsedAt);
        Assert.NotEqual(Guid.Empty, token.Id);
    }

    [Fact]
    public void TheLimitPerAccount_IsThreeAnHour_TheApprovedValue()
    {
        Assert.Equal(3, PasswordResetToken.MaxPerAccountPerHour);
    }

    [Fact]
    public void TwoTokens_NeverShareAnId()
    {
        Assert.NotEqual(New().Id, New().Id);
    }

    // ===== Vigencia =====

    [Fact]
    public void ANewToken_IsUsableRightAway_AndUntilTheLastInstantBeforeItExpires()
    {
        var token = New();

        Assert.True(token.IsUsableAt(Now));
        Assert.True(token.IsUsableAt(Now.AddMinutes(59)));
        Assert.True(token.IsUsableAt(token.ExpiresAt.AddTicks(-1)));
    }

    [Fact]
    public void AToken_StopsBeingUsableAtTheExactInstantItExpires()
    {
        var token = New();

        // El corte exacto: caducar es una fecha, no un cambio de estado, y a las 11:00 en punto ya no vale (control del test de arriba, un tick antes).
        Assert.False(token.IsUsableAt(token.ExpiresAt));
        Assert.False(token.IsUsableAt(token.ExpiresAt.AddMinutes(1)));
        Assert.False(token.IsUsableAt(Now.AddDays(1)));
    }

    // ===== Consumo =====

    [Fact]
    public void Consuming_MarksItUsed_AndItCannotBeUsedAgain()
    {
        var token = New();

        token.Consume(Now.AddMinutes(5));

        Assert.Equal(Now.AddMinutes(5), token.UsedAt);
        Assert.False(token.IsUsableAt(Now.AddMinutes(6)));
    }

    [Fact]
    public void ConsumingTwice_Fails_WithTheSingleInvalidCode()
    {
        var token = New();
        token.Consume(Now.AddMinutes(5));

        var exception = Assert.Throws<DomainException>(() => token.Consume(Now.AddMinutes(6)));

        Assert.Equal(IdentityErrors.PasswordResetInvalid, exception.Code);
        Assert.Equal(Now.AddMinutes(5), token.UsedAt); // el primer uso no se pisa
    }

    [Fact]
    public void ConsumingAnExpiredToken_Fails_WithTheSameCode()
    {
        var token = New();

        var exception = Assert.Throws<DomainException>(() => token.Consume(token.ExpiresAt));

        Assert.Equal(IdentityErrors.PasswordResetInvalid, exception.Code);
        Assert.Null(token.UsedAt);
    }

    [Fact]
    public void ConsumingAnInvalidatedToken_Fails_WithTheSameCode()
    {
        // Los tres motivos (usado, caducado, anulado) dan el MISMO código: quien lo intenta no aprende cuál fue (R8).
        var token = New();
        token.Invalidate(Now.AddMinutes(1));

        var exception = Assert.Throws<DomainException>(() => token.Consume(Now.AddMinutes(2)));

        Assert.Equal(IdentityErrors.PasswordResetInvalid, exception.Code);
    }

    [Fact]
    public void TheInvalidCode_IsTheApprovedContractValue()
    {
        Assert.Equal("password_reset.invalid", IdentityErrors.PasswordResetInvalid);
    }

    // ===== Anulación =====

    [Fact]
    public void Invalidating_MakesItUnusable()
    {
        var token = New();

        token.Invalidate(Now.AddMinutes(1));

        Assert.Equal(Now.AddMinutes(1), token.UsedAt);
        Assert.False(token.IsUsableAt(Now.AddMinutes(2)));
    }

    [Fact]
    public void Invalidating_AnAlreadyUsedToken_ChangesNothing_AndDoesNotFail()
    {
        var token = New();
        token.Consume(Now.AddMinutes(5));

        token.Invalidate(Now.AddMinutes(30));

        Assert.Equal(Now.AddMinutes(5), token.UsedAt); // la fecha del primer uso se conserva
    }

    [Fact]
    public void Invalidating_AnExpiredToken_DoesNotFail()
    {
        var token = New();

        token.Invalidate(Now.AddHours(3));

        Assert.False(token.IsUsableAt(Now.AddHours(3)));
    }

    // ===== Lo que Create no admite (fallos de programación, no reglas de negocio) =====

    [Fact]
    public void Create_RefusesAnEmptyAccount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordResetToken.Create(Guid.Empty, "hash", Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RefusesAnEmptyHash(string hash)
    {
        Assert.Throws<ArgumentException>(() => PasswordResetToken.Create(UserId, hash, Now));
    }

    [Fact]
    public void Create_RefusesAHashLongerThanTheColumn()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordResetToken.Create(UserId, new string('a', PasswordResetToken.TokenHashMaxLength + 1), Now));
    }

    [Fact]
    public void Create_AcceptsAHashAsLongAsTheColumn()
    {
        var token = PasswordResetToken.Create(UserId, new string('a', PasswordResetToken.TokenHashMaxLength), Now);

        Assert.Equal(PasswordResetToken.TokenHashMaxLength, token.TokenHash.Length);
    }

    [Fact]
    public void TheTokenHashColumn_FitsASha256InBase64()
    {
        // 32 bytes en base64 son 44 caracteres: la columna (64) cabe de sobra y la fuente única es la de Domain.
        Assert.True(PasswordResetToken.TokenHashMaxLength >= 44);
    }
}
