using OfiFlow.Application.Identity;
using OfiFlow.Domain.Common;

namespace OfiFlow.Application.Tests.Identity;

/// <summary>
/// Los dos correos del alta (spec 021 R4 y R6): funciones puras. Lo que se fija es lo que <b>no</b> cambia: asunto y texto son siempre los mismos y lo único variable es el enlace, así
/// que nadie puede conseguir que OfiFlow envíe un texto suyo (su nombre, el de su empresa) a la dirección de otra persona.
/// </summary>
public class RegistrationEmailsTests
{
    private const string Link = "https://app.ofiflow.example/auth/complete-registration#token=abc123";
    private const string RecoverLink = "https://app.ofiflow.example/auth/forgot-password";

    private static readonly Email Ana = Email.Create("ana@example.com");

    // ===== El correo con el enlace =====

    [Fact]
    public void TheRegistrationSubject_IsFixed_AndCarriesNoData()
    {
        var message = RegistrationEmail.Build(Ana, Link);

        Assert.Equal(RegistrationEmail.Subject, message.Subject);
        // El texto literal también: un asunto que cambia debe ser una decisión, no un descuido.
        Assert.Equal("Crea tu cuenta de OfiFlow", message.Subject);
        Assert.DoesNotContain("ana", message.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(message.Subject, c => char.IsControl(c));
    }

    [Fact]
    public void TheRegistrationMessage_IsAddressedToTheGivenAddress()
    {
        Assert.Equal(Ana, RegistrationEmail.Build(Ana, Link).To);
    }

    [Fact]
    public void TheRegistrationBody_IsTheSameForEveryone_ExceptForTheLink()
    {
        var forAna = RegistrationEmail.Build(Ana, Link).TextBody;
        var forLuis = RegistrationEmail.Build(Email.Create("luis.otro@example.org"), Link).TextBody;

        // Ni el nombre ni la dirección del destinatario entran en el texto, y menos aún el nombre de la persona o de la empresa que se escribieron al pedir el registro.
        Assert.Equal(forAna, forLuis);
    }

    [Fact]
    public void TheRegistrationLink_AppearsExactlyOnce_OnItsOwnLine_AndNoOtherUrlDoes()
    {
        var body = RegistrationEmail.Build(Ana, Link).TextBody;

        Assert.Equal(1, body.Split("\r\n").Count(line => line == Link));
        var urls = body.Split(' ', '\r', '\n').Where(word => word.StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal([Link], urls);
    }

    [Fact]
    public void TheRegistrationBody_SaysItExpiresInADay_IsSingleUse_AndThatNoAccountExistsUntilItIsOpened()
    {
        var body = RegistrationEmail.Build(Ana, Link).TextBody;

        Assert.Contains("caduca en 24 horas", body);
        Assert.Contains("solo sirve una vez", body);
        Assert.Contains("no existe ninguna cuenta", body);
        Assert.Contains("Si no lo has pedido tú, ignora este correo", body);
    }

    [Fact]
    public void TheRegistrationBody_DoesNotSayAnHour_ThatIsPasswordRecovery()
    {
        Assert.DoesNotContain("una hora", RegistrationEmail.Build(Ana, Link).TextBody);
    }

    [Fact]
    public void TheRegistrationBody_UsesCrlf_AndNeverALoneLineFeed()
    {
        var body = RegistrationEmail.Build(Ana, Link).TextBody;

        Assert.Contains("\r\n", body);
        Assert.DoesNotContain("\n", body.Replace("\r\n", string.Empty));
    }

    // ===== El aviso de «ya tienes una cuenta» =====

    [Fact]
    public void TheNoticeSubject_IsFixed_AndCarriesNoData()
    {
        var message = AccountExistsEmail.Build(Ana, RecoverLink);

        Assert.Equal(AccountExistsEmail.Subject, message.Subject);
        Assert.Equal("Ya tienes una cuenta de OfiFlow", message.Subject);
        Assert.DoesNotContain("ana", message.Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheNoticeMessage_IsAddressedToTheGivenAddress()
    {
        Assert.Equal(Ana, AccountExistsEmail.Build(Ana, RecoverLink).To);
    }

    [Fact]
    public void TheNoticeBody_IsTheSameForEveryone_ExceptForTheLink()
    {
        var forAna = AccountExistsEmail.Build(Ana, RecoverLink).TextBody;
        var forLuis = AccountExistsEmail.Build(Email.Create("luis.otro@example.org"), RecoverLink).TextBody;

        Assert.Equal(forAna, forLuis);
    }

    [Fact]
    public void TheNotice_CarriesExactlyOneLink_AndItHasNoToken()
    {
        var body = AccountExistsEmail.Build(Ana, RecoverLink).TextBody;

        var urls = body.Split(' ', '\r', '\n').Where(word => word.StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal([RecoverLink], urls);
        // Es la pantalla de recuperar la contraseña, que no concede nada por sí sola: ningún secreto en el aviso.
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#", body);
    }

    [Fact]
    public void TheNotice_SaysNothingWasCreated_AndWhatToDoIfNotYou()
    {
        var body = AccountExistsEmail.Build(Ana, RecoverLink).TextBody;

        Assert.Contains("ya existe una cuenta con ella", body);
        Assert.Contains("No se ha creado nada nuevo", body);
        Assert.Contains("Si no has sido tú, ignora este correo", body);
    }

    [Fact]
    public void TheNotice_UsesCrlf_AndNeverALoneLineFeed()
    {
        var body = AccountExistsEmail.Build(Ana, RecoverLink).TextBody;

        Assert.Contains("\r\n", body);
        Assert.DoesNotContain("\n", body.Replace("\r\n", string.Empty));
    }

    // ===== Los dos son distintos entre sí =====

    [Fact]
    public void TheTwoEmails_AreDifferentMessages_SoTheNoticeCannotBeMistakenForTheLink()
    {
        Assert.NotEqual(RegistrationEmail.Subject, AccountExistsEmail.Subject);
        Assert.NotEqual(RegistrationEmail.Build(Ana, Link).TextBody, AccountExistsEmail.Build(Ana, RecoverLink).TextBody);
    }
}
