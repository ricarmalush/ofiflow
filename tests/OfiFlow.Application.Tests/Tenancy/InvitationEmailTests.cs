using OfiFlow.Application.Tenancy;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Tenancy;

/// <summary>
/// El correo de una invitación (spec 017, R7, R9, R12, D7): una función pura, así que se prueba con tablas de casos.
/// </summary>
public class InvitationEmailTests
{
    private static readonly Email To = Email.Create("ana@example.com");
    private static readonly DateTime Expires = new(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
    private const string Link = "https://app.ofiflow.example/auth/accept-invitation#token=abc123";

    private static string BodyFor(string company = "Fontanería Pérez", TenantRole role = TenantRole.Technician) =>
        InvitationEmail.Build(To, company, role, Expires, Link).TextBody;

    [Fact]
    public void TheSubject_IsFixed_AndCarriesNoData()
    {
        var message = InvitationEmail.Build(To, "Cualquier\r\nBcc: otro@x.com", TenantRole.Admin, Expires, Link);

        Assert.Equal(InvitationEmail.Subject, message.Subject);
        Assert.DoesNotContain("Cualquier", message.Subject);
        Assert.DoesNotContain("ana@example.com", message.Subject);
        Assert.DoesNotContain(message.Subject, c => char.IsControl(c));
    }

    [Fact]
    public void TheMessage_IsAddressedToTheInvitedEmail()
    {
        Assert.Equal(To, InvitationEmail.Build(To, "X", TenantRole.Admin, Expires, Link).To);
    }

    [Theory]
    [InlineData(TenantRole.Owner, "propietario")]
    [InlineData(TenantRole.Admin, "administrador")]
    [InlineData(TenantRole.Manager, "responsable")]
    [InlineData(TenantRole.Technician, "técnico")]
    [InlineData(TenantRole.Employee, "oficina")]
    public void EveryRole_HasItsOwnLabel_TheSameOnesTheFrontendShows(TenantRole role, string label)
    {
        Assert.Contains($"con el rol de {label}.", BodyFor(role: role));
    }

    [Fact]
    public void EveryRoleInTheEnum_IsCovered_SoANewRoleCannotGoOutWithoutALabel()
    {
        foreach (var role in Enum.GetValues<TenantRole>())
        {
            var body = BodyFor(role: role);
            Assert.Contains("con el rol de ", body);
        }
    }

    [Fact]
    public void TheBody_HasTheCompany_TheLinkOnItsOwnLine_AndTheExpiryInSpanish()
    {
        var body = BodyFor();

        Assert.Contains("«Fontanería Pérez»", body);
        Assert.Contains("9 de noviembre de 2026", body);
        Assert.Equal(1, body.Split("\r\n").Count(line => line == Link));
        Assert.StartsWith("Hola:", body);
    }

    [Fact]
    public void TheLinkAppearsExactlyOnce_AndNoOtherUrlDoes()
    {
        var urls = BodyFor().Split(' ', '\r', '\n').Where(word => word.StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.Equal([Link], urls);
    }

    [Theory]
    [InlineData("Taller\r\nBcc: otro@x.com", "Taller  Bcc: otro@x.com")]
    [InlineData("Taller\nPaco", "Taller Paco")]
    [InlineData("Ta\tller\0", "Ta ller")]
    [InlineData("   Taller   ", "Taller")]
    public void ACompanyNameWithControlCharacters_StaysOnOneLine(string company, string shown)
    {
        var body = BodyFor(company);

        Assert.Contains($"«{shown}»", body);
        // Ni un salto de línea ni un carácter de control salió del nombre: el cuerpo tiene siempre el mismo número de líneas, las de la plantilla.
        Assert.Equal(11, body.Split("\r\n").Length);
    }

    [Fact]
    public void AllLineBreaks_AreCrLf()
    {
        var body = BodyFor();

        Assert.DoesNotContain("\n", body.Replace("\r\n", string.Empty));
    }

    [Fact]
    public void NothingTheInviterTypedIsInTheBody_ExceptTheirRoleChoiceAsALabel()
    {
        // El cuerpo no tiene más variables que la empresa, el rol (etiqueta), la caducidad y el enlace: con los mismos valores es idéntico, venga de quien venga.
        Assert.Equal(BodyFor(), BodyFor());
        Assert.Equal(InvitationEmail.Build(To, "X", TenantRole.Admin, Expires, Link).TextBody, InvitationEmail.Build(Email.Create("otra@example.com"), "X", TenantRole.Admin, Expires, Link).TextBody);
    }
}
