using OfiFlow.Application.Quotes.Commands.SendQuoteEmail;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Application.Tests.Quotes;

/// <summary>
/// La forma de la petición de mandar un presupuesto por correo (spec 026 R5): <b>una sola dirección</b>, sin nada que separe o envuelva direcciones ni caracteres de control, de 254 caracteres como
/// mucho. Es lo único que escribe quien manda y acaba en una cabecera del correo, así que la regla es más estricta que la de un email cualquiera.
/// </summary>
public class SendQuoteEmailCommandValidatorTests
{
    private static readonly SendQuoteEmailCommandValidator Validator = new();

    private static FluentValidation.Results.ValidationResult Validate(string? recipient, Guid? id = null) =>
        Validator.Validate(new SendQuoteEmailCommand(id ?? Guid.NewGuid(), recipient!));

    [Theory]
    [InlineData("cliente@ejemplo.es")]
    [InlineData("Ana.Perez+presupuestos@sub.ejemplo.co.uk")]
    [InlineData("a@b.co")]
    [InlineData("  cliente@ejemplo.es  ")]
    [InlineData("cliente@ejemplo.es\t")]
    [InlineData("ñandú@ejemplo.es")]
    [InlineData("ana@ejemplo.es\r\n")]
    public void AnAddress_IsAccepted(string recipient) => Assert.True(Validate(recipient).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("sin-arroba.es")]
    [InlineData("sin@punto")]
    [InlineData("a@@b.es")]
    [InlineData("a b@ejemplo.es")]
    [InlineData("a@ejemplo .es")]
    [InlineData("uno@ejemplo.es, dos@ejemplo.es")]
    [InlineData("uno@ejemplo.es,dos@ejemplo.es")]
    [InlineData("uno@ejemplo.es;dos@ejemplo.es")]
    [InlineData("uno,dos@ejemplo.es")]
    [InlineData("Ana <ana@ejemplo.es>")]
    [InlineData("<ana@ejemplo.es>")]
    [InlineData("\"ana\"@ejemplo.es")]
    [InlineData("ana(comentario)@ejemplo.es")]
    [InlineData("ana@[127.0.0.1].es")]
    [InlineData("ana\\@ejemplo.es")]
    [InlineData("ana@ejemplo.es\r\nBcc: otro@ejemplo.es")]
    [InlineData("ana\0@ejemplo.es")]
    [InlineData("ana\u0007@ejemplo.es")]
    public void ANonSingleOrUnsafeAddress_IsRefused(string? recipient) => Assert.False(Validate(recipient).IsValid);

    [Fact]
    public void TheRefusal_UsesTheUsualEmailFieldCode_AndNeverRepeatsTheValue()
    {
        var result = Validate("uno@ejemplo.es, dos@ejemplo.es");

        var error = Assert.Single(result.Errors);
        Assert.Equal(nameof(SendQuoteEmailCommand.Recipient), error.PropertyName);
        Assert.Equal(CommonErrors.EmailInvalid, error.ErrorCode);
        Assert.DoesNotContain("ejemplo", error.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLongestAllowedAddress_IsAccepted_AndOneCharacterMoreIsNot()
    {
        const string tail = "@ejemplo.es";
        var longest = new string('a', Quote.EmailRecipientMaxLength - tail.Length) + tail;
        var tooLong = "a" + longest;

        Assert.Equal(254, longest.Length);
        Assert.True(Validate(longest).IsValid);
        Assert.False(Validate(tooLong).IsValid);
    }

    [Fact]
    public void TheLimit_CountsWithoutTheSurroundingSpaces()
    {
        const string tail = "@ejemplo.es";
        var longest = new string('a', Quote.EmailRecipientMaxLength - tail.Length) + tail;

        Assert.True(Validate("   " + longest + "   ").IsValid);
    }

    [Fact]
    public void AnEmptyQuoteId_IsRefused() => Assert.False(Validate("cliente@ejemplo.es", Guid.Empty).IsValid);

    [Fact]
    public void TheMaximumLength_IsTheOneOfARfcAddress() => Assert.Equal(254, Quote.EmailRecipientMaxLength);
}
