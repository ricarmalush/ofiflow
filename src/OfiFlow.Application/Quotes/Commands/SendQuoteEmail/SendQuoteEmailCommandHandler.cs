using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Mail;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Application.Quotes.Commands.SendQuoteEmail;

/// <summary>
/// Manda un presupuesto por correo (spec 026). Orden de comprobaciones (R7), de lo más barato a lo más caro y <b>sin tocar el servidor de correo hasta el final</b>: el permiso ya lo comprobó el
/// <c>AuthorizationBehavior</c> y la forma de la petición el <c>ValidationBehavior</c>; aquí, correo configurado (<c>400</c>), presupuesto de la empresa (<c>404</c>), con líneas (<c>400</c>),
/// perfil fiscal (<c>400</c>), generar el PDF, <b>enviar</b> y por último <b>anotar</b>.
/// </summary>
/// <remarks>
/// Se envía <b>antes</b> de anotar (R8, D6; ADR-020 C1): si el envío falla no hay nada que deshacer. Y anotar no puede estropear un envío ya hecho: si el correo salió, la respuesta es <c>200</c> aunque
/// guardar la fecha falle (un <c>409</c> tras un correo entregado llevaría a reenviarlo). No se reintenta la anotación: el presupuesto se lee <b>después</b> de enviar y se guarda de inmediato, así
/// que solo un cambio simultáneo en esos milisegundos la haría chocar, y el correo ya salió. Nada de la dirección, el asunto, el cuerpo ni el PDF llega a un registro (R20, ADR-009 R5).
/// </remarks>
public sealed partial class SendQuoteEmailCommandHandler(
    IApplicationDbContext dbContext,
    QuotePdfBuilder pdfBuilder,
    IEmailSender emailSender,
    ICurrentUser currentUser,
    ITenantContext tenantContext,
    TimeProvider timeProvider,
    ILogger<SendQuoteEmailCommandHandler> logger)
    : IRequestHandler<SendQuoteEmailCommand, QuoteEmailSentDto>
{
    public async Task<QuoteEmailSentDto> Handle(SendQuoteEmailCommand request, CancellationToken cancellationToken)
    {
        // R10: sin servidor de correo no hay alternativa útil. Es lo primero: no se lee ni se genera nada que no se va a poder mandar.
        if (!emailSender.IsEnabled)
        {
            throw new BusinessRuleException(QuoteErrors.EmailDisabled);
        }

        // El validador ya comprobó que es una sola dirección; aquí solo se recortan los espacios de los extremos (R5).
        var recipient = Email.Create(request.Recipient.Trim());

        var source = await pdfBuilder.LoadAsync(request.Id, cancellationToken);

        // R3: un presupuesto sin líneas no se manda. Va entre el 404 y el perfil: es lo que dice el orden de R7.
        if (source.Quote.Items.Count == 0)
        {
            throw new BusinessRuleException(QuoteErrors.EmailRequiresItems);
        }

        var document = await pdfBuilder.RenderAsync(source, cancellationToken);

        // R11, R13, R14: la plantilla fija, con el email de la empresa como dirección de respuesta y el PDF adjunto. Un perfil guardado siempre tiene nombre fiscal (lo exige el PUT).
        var message = QuoteEmail.Build(
            recipient,
            document.Quote.Number,
            document.Company.LegalName!,
            document.Quote.TotalCents,
            document.Quote.ValidUntil,
            document.Company.Email is { } companyEmail ? Email.Create(companyEmail) : null,
            document.File);

        try
        {
            await emailSender.SendAsync(message, cancellationToken);
        }
        catch (EmailDeliveryException exception)
        {
            // Solo la categoría: ni la dirección ni el texto del servidor de correo (R9, R20). A quien lo manda se le dice que no salió, sin el motivo, y no se anota nada.
            LogQuoteEmailFailed(logger, request.Id, tenantContext.TenantId, currentUser.UserId, exception.Category.ToString());
            throw new ExternalServiceException(QuoteErrors.EmailNotSent);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var recorded = await TryRecordAsync(request.Id, now, cancellationToken);

        LogQuoteEmailSent(logger, request.Id, tenantContext.TenantId, currentUser.UserId, recorded);

        return new QuoteEmailSentDto(now);
    }

    /// <summary>
    /// Anota el último envío (R19). Devuelve si se pudo: el correo ya salió y eso no se deshace (R8). Solo se traga el choque de concurrencia y la desaparición del presupuesto; cualquier otro fallo de
    /// la base de datos sigue siendo un error de verdad.
    /// </summary>
    private async Task<bool> TryRecordAsync(Guid quoteId, DateTime now, CancellationToken cancellationToken)
    {
        // Sin las líneas: no cambian, y no hace falta cargarlas para anotar una fecha. El filtro global sigue limitando la lectura a la empresa.
        var quote = await dbContext.Quotes.FirstOrDefaultAsync(q => q.Id == quoteId, cancellationToken);
        if (quote is null)
        {
            // Un borrador eliminado entre el envío y la anotación: no hay dónde anotar.
            return false;
        }

        quote.RecordEmailSent(now, currentUser.UserId!.Value);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    // Solo Ids y la categoría: ni la dirección, ni el asunto, ni el fichero, ni el texto del servidor (R20, ADR-009 R5).
    [LoggerMessage(EventId = SecurityEventIds.QuoteEmailSent, Level = LogLevel.Information,
        Message = "Presupuesto mandado por correo y aceptado por el servidor de correo. QuoteId {QuoteId}, TenantId {TenantId}, enviado por {UserId}, fecha anotada {Recorded}")]
    private static partial void LogQuoteEmailSent(ILogger logger, Guid quoteId, Guid tenantId, Guid? userId, bool recorded);

    [LoggerMessage(EventId = SecurityEventIds.QuoteEmailFailed, Level = LogLevel.Warning,
        Message = "No se pudo mandar un presupuesto por correo. QuoteId {QuoteId}, TenantId {TenantId}, solicitado por {UserId}, categoría {Category}")]
    private static partial void LogQuoteEmailFailed(ILogger logger, Guid quoteId, Guid tenantId, Guid? userId, string category);
}
