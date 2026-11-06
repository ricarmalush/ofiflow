using System.Diagnostics.CodeAnalysis;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Commands.CompleteRegistration;

/// <summary>
/// Completar el registro con el token del correo (spec 021, ADR-023). <b>Aquí nace la cuenta</b> (D1): hasta que se abre el enlace no existía ninguna. Cuatro cosas cambian y las cuatro se
/// confirman <b>juntas</b>, en un único <c>SaveChangesAsync</c> (R10): el pendiente pasa a canjeado, y nacen la cuenta de acceso, la empresa con su usuario (con las condiciones aceptadas) y la
/// pertenencia como <c>Owner</c>. Si algo falla, no queda nada: ni una empresa sin dueño, ni un pendiente gastado sin cuenta.
/// </summary>
/// <remarks>
/// <b>No abre sesión</b> (R12, D6): se entra con el acceso de siempre. Un enlace de correo no es una llave de entrada.
/// </remarks>
public sealed partial class CompleteRegistrationCommandHandler(
    IPendingRegistrationService pendingRegistrations,
    IIdentityService identityService,
    IApplicationDbContext dbContext,
    ILogger<CompleteRegistrationCommandHandler> logger)
    : IRequestHandler<CompleteRegistrationCommand>
{
    public async Task Handle(CompleteRegistrationCommand request, CancellationToken cancellationToken)
    {
        // Primero el token, que es barato: uno que no sirve se rechaza sin pagar el hash lento de la contraseña (este endpoint es anónimo).
        var consumption = await pendingRegistrations.ConsumeAsync(request.Token, cancellationToken);

        if (consumption.Outcome != PendingRegistrationConsumeOutcome.Consumed)
        {
            // R11: el MISMO error para los cuatro motivos. El motivo real solo va al registro.
            Reject(consumption.RejectedReason ?? "Unknown");
        }

        var userId = Guid.NewGuid();

        // Sin confirmar: la cuenta de acceso se confirma con el resto. Falla solo si el email ya está ocupado (alguien lo registró entre que se comprobó el pendiente y ahora): es el mismo
        // «este enlace ya no sirve», no un error que la persona pueda corregir, y no se ha confirmado nada todavía.
        var identityResult = await identityService.CreateUserAsync(userId, consumption.Email!, request.NewPassword, cancellationToken);
        if (!identityResult.Succeeded)
        {
            Reject("EmailTaken");
        }

        var tenant = Tenant.Create(consumption.CompanyName!);
        var user = User.Create(userId, consumption.UserName!, Email.Create(consumption.Email!));
        user.AcceptTerms(consumption.TermsVersion!, consumption.TermsAcceptedAt!.Value);
        var tenantUser = TenantUser.CreateOwner(tenant.Id, userId);

        dbContext.Tenants.Add(tenant);
        dbContext.Users.Add(user);
        dbContext.TenantUsers.Add(tenantUser);

        try
        {
            // La única confirmación (R10). R13: si otra petición canjeó el mismo token a la vez, la versión de fila del pendiente hace fallar a esta (o el índice único del email, si llegó a
            // crear la cuenta antes), y con ella la empresa y el usuario: gana una sola. Se capturan juntas la excepción de concurrencia y cualquier otra de la base de datos, como al aceptar
            // una invitación: capturar solo la de concurrencia dejaría escapar la otra como un 500.
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            Reject("Concurrent");
        }

        LogCompleted(logger, tenant.Id, userId);
    }

    [DoesNotReturn]
    private void Reject(string reason)
    {
        LogRejected(logger, reason);
        throw new DomainException(IdentityErrors.RegistrationInvalid);
    }

    // Nunca el token, la contraseña, el email ni los nombres (ADR-009 R5). La IP la añade el scope de la API.
    [LoggerMessage(EventId = SecurityEventIds.RegistrationCompleted, Level = LogLevel.Information,
        Message = "Registro completado: nueva cuenta. TenantId {TenantId}, UserId {UserId}")]
    private static partial void LogCompleted(ILogger logger, Guid tenantId, Guid userId);

    [LoggerMessage(EventId = SecurityEventIds.RegistrationRejected, Level = LogLevel.Warning,
        Message = "Registro rechazado al completarlo. Motivo {Reason}")]
    private static partial void LogRejected(ILogger logger, string reason);
}
