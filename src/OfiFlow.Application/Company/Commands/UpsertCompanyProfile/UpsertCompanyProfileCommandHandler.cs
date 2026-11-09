using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Company;
using OfiFlow.Domain.Customers;

namespace OfiFlow.Application.Company.Commands.UpsertCompanyProfile;

public sealed partial class UpsertCompanyProfileCommandHandler(
    IApplicationDbContext dbContext,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    ILogger<UpsertCompanyProfileCommandHandler> logger) : IRequestHandler<UpsertCompanyProfileCommand>
{
    public async Task Handle(UpsertCompanyProfileCommand request, CancellationToken cancellationToken)
    {
        // Los objetos de valor son los que garantizan el NIF y el código postal; el validador ya los comprobó con las mismas reglas.
        var taxId = SpanishTaxId.Create(request.TaxId);
        var postalCode = SpanishPostalCode.Create(request.PostalCode);
        var phone = string.IsNullOrWhiteSpace(request.Phone) ? null : PhoneNumber.Create(request.Phone.Trim());
        var email = string.IsNullOrWhiteSpace(request.Email) ? null : Email.Create(request.Email.Trim());

        // El filtro global limita la búsqueda a la empresa del token: el perfil de otra no existe para esta petición (R8).
        var profile = await dbContext.CompanyProfiles.FirstOrDefaultAsync(cancellationToken);

        if (profile is not null)
        {
            profile.Update(request.LegalName, taxId, request.AddressLine, postalCode, request.City, request.Province, phone, email);
            await dbContext.SaveChangesAsync(cancellationToken);
            LogChanged(logger, currentUser.UserId, tenantContext.TenantId, created: false);
            return;
        }

        var created = CompanyProfile.Create(tenantContext.TenantId, request.LegalName, taxId, request.AddressLine, postalCode, request.City, request.Province, phone, email);
        dbContext.CompanyProfiles.Add(created);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            LogChanged(logger, currentUser.UserId, tenantContext.TenantId, created: true);
        }
        catch (DbUpdateException)
        {
            // Otra petición de la misma empresa creó el perfil entre la lectura y este guardado: el índice único lo impidió. Se saca el intento fallido del seguimiento,
            // se relee el que ganó y se sustituye con lo que llegó aquí (gana la última escritura, D9). Si no hay ninguno, el fallo era otro y no se esconde.
            dbContext.CompanyProfiles.Remove(created);

            var existing = await dbContext.CompanyProfiles.FirstOrDefaultAsync(cancellationToken);
            if (existing is null)
            {
                throw;
            }

            existing.Update(request.LegalName, taxId, request.AddressLine, postalCode, request.City, request.Province, phone, email);
            await dbContext.SaveChangesAsync(cancellationToken);
            LogChanged(logger, currentUser.UserId, tenantContext.TenantId, created: false);
        }
    }

    // Quién y cuándo, nunca qué (ADR-009 R5): ni el NIF, ni la dirección, ni el teléfono, ni el email, ni los valores anteriores. La IP la añade el scope de la API.
    [LoggerMessage(EventId = SecurityEventIds.CompanyProfileChanged, Level = LogLevel.Information,
        Message = "Perfil fiscal guardado. UserId {UserId}, TenantId {TenantId}, creado {Created}")]
    private static partial void LogChanged(ILogger logger, Guid? userId, Guid tenantId, bool created);
}
