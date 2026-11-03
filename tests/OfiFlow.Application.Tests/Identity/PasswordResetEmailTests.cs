using OfiFlow.Application.Identity;
using OfiFlow.Domain.Common;

namespace OfiFlow.Application.Tests.Identity;

/// <summary>
/// El correo de restablecer la contraseña (spec 018 R5): una función pura. Lo que se fija es lo que <b>no</b> cambia: asunto y texto son siempre los mismos y lo único variable es el
/// enlace, así que nadie puede conseguir que OfiFlow envíe un texto suyo a la cuenta de otra persona.
/// </summary>
public class PasswordResetEmailTests
{
    private const string Link = "https://app.ofiflow.example/auth/reset-password#token=abc123";

    private static readonly Email Ana = Email.Create("ana@example.com");

    [Fact]
    public void TheSubject_IsFixed_AndCarriesNoData()
    {
        var message = PasswordResetEmail.Build(Ana, Link);

        Assert.Equal(PasswordResetEmail.Subject, message.Subject);
        // El texto literal también: un asunto que cambia (o que se acorta hasta no decir de qué servicio viene) debe ser una decisión, no un descuido.
        Assert.Equal("Restablece tu contraseña de OfiFlow", message.Subject);
        Assert.DoesNotContain("ana", message.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(message.Subject, c => char.IsControl(c));
    }

    [Fact]
    public void TheMessage_IsAddressedToTheGivenAddress()
    {
        Assert.Equal(Ana, PasswordResetEmail.Build(Ana, Link).To);
    }

    [Fact]
    public void TheBody_IsTheSameForEveryone_ExceptForTheLink()
    {
        var forAna = PasswordResetEmail.Build(Ana, Link).TextBody;
        var forLuis = PasswordResetEmail.Build(Email.Create("luis.otro@example.org"), Link).TextBody;

        // Ni el nombre ni la dirección del destinatario entran en el texto: es idéntico para dos personas distintas.
        Assert.Equal(forAna, forLuis);
    }

    [Fact]
    public void TheLinkAppearsExactlyOnce_OnItsOwnLine_AndNoOtherUrlDoes()
    {
        var body = PasswordResetEmail.Build(Ana, Link).TextBody;

        Assert.Equal(1, body.Split("\r\n").Count(line => line == Link));
        var urls = body.Split(' ', '\r', '\n').Where(word => word.StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal([Link], urls);
    }

    [Fact]
    public void TheBody_SaysItExpiresInAnHour_IsSingleUse_AndWhatToDoIfNotAsked()
    {
        var body = PasswordResetEmail.Build(Ana, Link).TextBody;

        Assert.Contains("caduca en una hora", body);
        Assert.Contains("solo sirve una vez", body);
        Assert.Contains("Si no lo has pedido tú, ignora este correo", body);
    }

    [Fact]
    public void AllLineBreaks_AreCrLf_AndTheTemplateHasAFixedNumberOfLines()
    {
        var body = PasswordResetEmail.Build(Ana, Link).TextBody;

        Assert.DoesNotContain("\n", body.Replace("\r\n", string.Empty));
        Assert.Equal(11, body.Split("\r\n").Length);
    }

    [Fact]
    public void TheBody_NeverContainsAPassword_OrAnythingThatLooksLikeOne()
    {
        // NEVER (spec 018): ni la contraseña ni una temporal salen por correo. El correo solo lleva un enlace.
        var body = PasswordResetEmail.Build(Ana, Link).TextBody.ToLowerInvariant();

        Assert.DoesNotContain("contraseña temporal", body);
        Assert.DoesNotContain("tu nueva contraseña es", body);
    }
}
