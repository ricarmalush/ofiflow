using System.Text.RegularExpressions;
using OfiFlow.Application.Common.Mail;
using OfiFlow.Application.Quotes;

namespace OfiFlow.Api.Tests.Architecture;

/// <summary>
/// Lo que protege el correo de un presupuesto (spec 026, bloque 2) y que ningún test de camino feliz ve hasta que es tarde: el límite por persona está en el endpoint y solo gasta quien tiene el permiso,
/// la dirección no tiene dónde guardarse, el correo no se manda sin comprobar que hay servidor de correo, el handler lee solo a través del filtro por empresa y los adjuntos no entran por otro camino.
/// Son escaneos del código (ADR-009).
/// </summary>
public class QuoteEmailArchitectureTests
{
    private const string Endpoints = "src/OfiFlow.Api/Endpoints/QuoteEndpoints.cs";
    private const string Handler = "src/OfiFlow.Application/Quotes/Commands/SendQuoteEmail/SendQuoteEmailCommandHandler.cs";

    private static string Code(string text) =>
        string.Join('\n', text.Split('\n').Select(line => line.TrimStart()).Where(line => !line.StartsWith("//", StringComparison.Ordinal)));

    private static string EmailEndpoint()
    {
        var code = Code(SourceCode.ReadText(Endpoints));
        var start = code.IndexOf("group.MapPost(\"/{id:guid}/email\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "No se encuentra el endpoint del correo.");
        var end = code.IndexOf("group.Map", start + 10, StringComparison.Ordinal);

        return code[start..end];
    }

    [Fact]
    public void TheEndpoint_HasBothLimits_TheOneByPersonOnlyForWhoHasThePermission_AndIsNeverCached()
    {
        var endpoint = EmailEndpoint();

        Assert.Contains(".RequireRateLimiting(RateLimiting.QuoteEmail)", endpoint, StringComparison.Ordinal);
        Assert.Contains("QuoteEmailThrottle throttle", endpoint, StringComparison.Ordinal);
        Assert.Contains("throttle.TryAcquire(userId, out var retryAfter)", endpoint, StringComparison.Ordinal);
        Assert.Contains("RolePermissions.Has(role, Permission.QuotesIssue)", endpoint, StringComparison.Ordinal);
        Assert.Contains("Headers.CacheControl = \"no-store\"", endpoint, StringComparison.Ordinal);

        // El cupo se consulta ANTES de enviar la orden: si no, un envío más allá del límite ya habría salido.
        Assert.True(endpoint.IndexOf("throttle.TryAcquire", StringComparison.Ordinal) < endpoint.IndexOf("sender.Send(", StringComparison.Ordinal));
    }

    [Fact]
    public void TheEndpoint_ReadsNothingFromTheRequestButTheRecipient_AndNeverTheCompany()
    {
        var endpoint = EmailEndpoint();

        // La petición solo trae la dirección: ni asunto, ni cuerpo, ni adjunto, ni empresa, ni remitente (spec 026 D9, D10, D12).
        var request = Regex.Match(Code(SourceCode.ReadText(Endpoints)), @"internal sealed record SendQuoteEmailRequest\(([^)]*)\)");
        Assert.True(request.Success);
        Assert.Equal("string Recipient", request.Groups[1].Value.Trim());
        foreach (var forbidden in new[] { "Subject", "Body", "Attachment", "TenantId", "From", "ReplyTo" })
        {
            Assert.DoesNotContain(forbidden, request.Groups[1].Value, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("TenantId", endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommandCarriesOnlyTheQuoteAndTheAddress()
    {
        var properties = typeof(OfiFlow.Application.Quotes.Commands.SendQuoteEmail.SendQuoteEmailCommand).GetProperties()
            .Where(p => p.DeclaringType == typeof(OfiFlow.Application.Quotes.Commands.SendQuoteEmail.SendQuoteEmailCommand) && p.Name != "EqualityContract")
            .Select(p => p.Name).Order().ToList();

        Assert.Equal(["Id", "Recipient"], properties);
    }

    [Fact]
    public void TheHandler_ChecksThatThereIsAMailServerFirst_ReadsThroughTheTenantFilter_AndRecordsAfterSending()
    {
        var code = Code(SourceCode.ReadText(Handler));

        // Primero «¿hay servidor de correo?»: antes de leer, generar o enviar nada.
        var enabled = code.IndexOf("emailSender.IsEnabled", StringComparison.Ordinal);
        Assert.True(enabled >= 0 && enabled < code.IndexOf("pdfBuilder.LoadAsync", StringComparison.Ordinal), "El handler tiene que comprobar IsEnabled antes de leer nada.");

        // Se envía antes de anotar (ADR-020 C1).
        Assert.True(code.IndexOf("emailSender.SendAsync", StringComparison.Ordinal) < code.IndexOf("TryRecordAsync(request.Id", StringComparison.Ordinal));

        // El filtro global por empresa hace de única barrera: nada de saltárselo ni de pasar la empresa a mano a una consulta.
        Assert.DoesNotContain("IgnoreQueryFilters", code, StringComparison.Ordinal);
        Assert.DoesNotContain(".TenantId ==", code, StringComparison.Ordinal);

        // Una sola confirmación, y solo del último envío.
        Assert.Equal(1, Regex.Matches(code, @"SaveChangesAsync\(").Count);
    }

    [Fact]
    public void TheAddress_HasNoPlaceToBeStored()
    {
        // D4: se guarda cuándo y quién, nunca a quién. Ni una columna ni una propiedad de destinatario en el modelo de datos, ni en lo que sale por la API.
        // El modelo no tiene ninguna propiedad de destinatario (la constante con el máximo de la dirección es solo un número, no un dato guardado)...
        Assert.DoesNotContain(typeof(OfiFlow.Domain.Quotes.Quote).GetProperties(), p => p.Name.Contains("Recipient", StringComparison.OrdinalIgnoreCase));

        // ...ni la configuración de EF la mapea a una columna.
        foreach (var file in SourceCode.Files("OfiFlow.Infrastructure/Persistence/Configurations"))
        {
            Assert.DoesNotContain("Recipient", file.Content, StringComparison.OrdinalIgnoreCase);
        }

        var dto = Code(SourceCode.ReadText("src/OfiFlow.Application/Quotes/QuoteDto.cs"));
        Assert.DoesNotContain("Recipient", dto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EmailedBy", dto, StringComparison.Ordinal);

        // La migración se busca con FilesMatching y no con Files: Files descarta a propósito toda carpeta Migrations (código generado por EF) y, con una ruta que mezcla «/» y «\»,
        // en Windows no la descartaba pero en Linux sí, así que el test pasaba en local y fallaba en el CI. FilesMatching no pasa por esa exclusión y da rutas con «/» en los dos.
        var migration = Assert.Single(SourceCode.FilesMatching("OfiFlow.Infrastructure/Persistence/Migrations", "*_AddQuoteEmail.cs"));
        Assert.DoesNotContain("Recipient", SourceCode.ReadText(migration), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheOnlyUsersOfTheAttachment_AreTheQuoteEmailTemplateAndTheTransport()
    {
        // D12: un adjunto es una vía más de fuga. Solo la plantilla del presupuesto lo construye y solo el transporte SMTP lo monta.
        var builders = SourceCode.Files("OfiFlow.Application").Concat(SourceCode.Files("OfiFlow.Infrastructure")).Concat(SourceCode.Files("OfiFlow.Api"))
            .Where(f => f.Content.Contains("new EmailAttachment(", StringComparison.Ordinal))
            .Select(f => f.Path.Replace('\\', '/').Split('/').Last())
            .Order()
            .ToList();

        Assert.Equal(["QuoteEmail.cs"], builders);

        var mounters = SourceCode.Files("OfiFlow.Infrastructure")
            .Where(f => f.Content.Contains(".Attachment", StringComparison.Ordinal) && f.Content.Contains("Multipart", StringComparison.Ordinal))
            .Select(f => f.Path.Replace('\\', '/').Split('/').Last())
            .ToList();
        Assert.Equal(["SmtpEmailSender.cs"], mounters);
    }

    [Fact]
    public void TheTemplate_TakesNothingTheUserTyped()
    {
        // La firma fija qué entra en el correo: ni título, ni notas, ni cliente, ni descripciones (R11). Si alguien añade un parámetro, este test obliga a decidirlo a propósito.
        var parameters = typeof(QuoteEmail).GetMethod(nameof(QuoteEmail.Build))!.GetParameters().Select(p => p.Name).ToList();

        Assert.Equal(["to", "quoteNumber", "companyLegalName", "totalCents", "validUntil", "companyEmail", "pdf"], parameters);
        Assert.Equal(["application/pdf"], EmailAttachment.AllowedContentTypes);
    }

    [Fact]
    public void TheSecurityEvents_AreTheTwoOfTheSpec()
    {
        Assert.Equal(1410, OfiFlow.Application.Common.Logging.SecurityEventIds.QuoteEmailSent);
        Assert.Equal(1411, OfiFlow.Application.Common.Logging.SecurityEventIds.QuoteEmailFailed);
    }

    [Fact]
    public void TheCodesOfTheErrors_AreInTheDictionary_WithTheirStatus()
    {
        var resx = SourceCode.ReadText("src/OfiFlow.Api/Resources/ErrorMessages.resx");
        foreach (var code in new[] { OfiFlow.Domain.Quotes.QuoteErrors.EmailDisabled, OfiFlow.Domain.Quotes.QuoteErrors.EmailRequiresItems, OfiFlow.Domain.Quotes.QuoteErrors.EmailNotSent })
        {
            Assert.Contains($"<data name=\"{code}\"", resx, StringComparison.Ordinal);
        }

        Assert.Equal("quote.email_disabled", OfiFlow.Domain.Quotes.QuoteErrors.EmailDisabled);
        Assert.Equal("quote.email_requires_items", OfiFlow.Domain.Quotes.QuoteErrors.EmailRequiresItems);
        Assert.Equal("quote.email_not_sent", OfiFlow.Domain.Quotes.QuoteErrors.EmailNotSent);
    }
}
