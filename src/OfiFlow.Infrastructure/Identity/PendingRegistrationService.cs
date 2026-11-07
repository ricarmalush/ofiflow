using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Security;
using OfiFlow.Domain.Identity;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// Los registros pendientes de confirmar (spec 021, ADR-023). <b>Este es el único fichero que lee la tabla</b> (como <see cref="PasswordResetService"/> con la suya): un test de arquitectura lo
/// vigila. Los pendientes se buscan por el hash del token, que es secreto y único, y no tienen filtro de empresa porque todavía no hay ninguna (R4).
/// </summary>
/// <remarks>
/// Hace tres cosas: <b>pedir</b> un registro, <b>canjearlo</b> y la <b>purga</b> de los caducados (R18). Como la recuperación de contraseña, ninguna de las dos primeras confirma el canje
/// por su cuenta: lo hace el caso de uso, junto con la creación de la empresa y del usuario.
/// </remarks>
internal sealed class PendingRegistrationService(ApplicationDbContext dbContext, TimeProvider timeProvider) : IPendingRegistrationService, IPendingRegistrationPurger
{
    public async Task<PendingRegistrationIssue> IssueAsync(string email, string companyName, string userName, string termsVersion, CancellationToken cancellationToken)
    {
        // La búsqueda por el email normalizado, la misma regla que el registro y el login (ADR-004): «Ana@Example.com» y «ana@example.com» son el mismo email.
        var normalizedEmail = email.Trim().ToUpperInvariant();

        // R6: un email que YA tiene cuenta no crea nada. Incluye la cuenta de un operador de la plataforma: es otra identidad (spec 011) pero ocupa el mismo email, y para quien pide
        // el registro se trata igual que cualquier otra: nada nuevo y un aviso.
        if (await dbContext.ApplicationUsers.AnyAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken))
        {
            return new PendingRegistrationIssue(PendingRegistrationIssueOutcome.AccountExists);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // R7: como mucho MaxPerEmailPerHour pendientes por email en la última hora, vengan de la IP que vengan. Se cuentan los CREADOS (canjeados, anulados o no): pedir, anular y volver a pedir
        // también cuenta, porque lo que se limita es el correo que le llega a la persona.
        var since = now - TimeSpan.FromHours(1);
        var recent = await dbContext.PendingRegistrations.CountAsync(r => r.NormalizedEmail == normalizedEmail && r.CreatedAt > since, cancellationToken);
        if (recent >= PendingRegistration.MaxPerEmailPerHour)
        {
            return new PendingRegistrationIssue(PendingRegistrationIssueOutcome.Throttled);
        }

        // R5: un solo vigente por email. Se anulan TODOS los que no se hayan canjeado ni anulado, también los caducados: una fila caducada sin canjear sigue teniendo UsedAt nulo y chocaría con el
        // índice único filtrado al insertar la nueva.
        var open = await dbContext.PendingRegistrations
            .Where(r => r.NormalizedEmail == normalizedEmail && r.UsedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var pending in open)
        {
            pending.Invalidate(now);
        }

        var (value, hash) = OpaqueToken.Generate();
        dbContext.PendingRegistrations.Add(PendingRegistration.Create(email, companyName, userName, termsVersion, hash, now));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Una carrera: otra petición para el mismo email insertó su pendiente vigente justo antes (el índice único filtrado lo impide) o canjeó el anterior justo ahora (la versión de
            // fila cambió). Un error 500 aquí solo ocurriría a veces y según el email: un oráculo raro pero real. Se trata como «ahora no»: no se crea nada y la persona puede volver a pedirlo.
            return new PendingRegistrationIssue(PendingRegistrationIssueOutcome.Throttled);
        }

        // El valor en claro sale de aquí UNA vez, para el correo. En la base de datos solo queda su hash.
        return new PendingRegistrationIssue(PendingRegistrationIssueOutcome.Issued, value);
    }

    public async Task<PendingRegistrationConsumption> ConsumeAsync(string token, CancellationToken cancellationToken)
    {
        // Por el hash: el token en claro no se guarda en ningún sitio y el hash es único (índice) y de 256 bits, así que esta búsqueda no distingue emails ni empresas (R4).
        var hash = OpaqueToken.Hash(token);
        var entity = await dbContext.PendingRegistrations.FirstOrDefaultAsync(r => r.TokenHash == hash, cancellationToken);

        // R11: los motivos reales solo viajan hasta el registro; el caso de uso responde lo mismo para todos.
        if (entity is null)
        {
            return Rejected("Unknown");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // «Canjeado» y «anulado por otro pedido» dejan la misma marca (UsedAt, como en el dominio: Invalidate no distingue), y se cuentan juntos.
        if (entity.UsedAt is not null)
        {
            return Rejected("UsedOrReplaced");
        }

        if (!entity.IsUsableAt(now))
        {
            return Rejected("Expired");
        }

        // R11: el email ya tiene cuenta DESDE QUE SE PIDIÓ (alguien la creó por otra vía: una invitación, por ejemplo). Crear otra con el mismo email chocaría con el índice único del email, y
        // es mejor decirlo aquí, antes de marcar nada, que fallar al confirmar.
        if (await dbContext.ApplicationUsers.AnyAsync(u => u.NormalizedEmail == entity.NormalizedEmail, cancellationToken))
        {
            return Rejected("EmailTaken");
        }

        // R13: se marca como canjeado SIN confirmar. La confirmación es la del caso de uso y la decide la versión de fila: dos canjes a la vez leyeron «sin usar», los dos llegan aquí,
        // y solo uno de los dos UPDATE encuentra la fila como la leyó.
        entity.Consume(now);

        // No hace falta anular «los demás vigentes» de ese email: el índice único filtrado garantiza que, si esta fila estaba viva, no había otra viva con el mismo email (R5).
        return new PendingRegistrationConsumption(
            PendingRegistrationConsumeOutcome.Consumed,
            entity.Email,
            entity.CompanyName,
            entity.UserName,
            entity.TermsVersion,
            entity.TermsAcceptedAt);
    }

    private static PendingRegistrationConsumption Rejected(string reason) =>
        new(PendingRegistrationConsumeOutcome.Rejected, RejectedReason: reason);

    public Task<int> PurgeBatchAsync(DateTime cutoff, int batchSize, CancellationToken cancellationToken) =>
        // Set-based y en LINQ (no SQL a mano: ADR-009 R1). Lo MÁS antiguo primero y con tope de lote. Solo mira ExpiresAt (índice IX_PendingRegistrations_ExpiresAt): canjeado o no da
        // igual, porque un pendiente caducado hace más del margen ya no puede usarse, y son datos personales de gente que quizá nunca llegó a tener cuenta (R18). Uno vigente no ha
        // caducado, así que nunca cumple esta condición.
        dbContext.PendingRegistrations
            .Where(r => r.ExpiresAt < cutoff)
            .OrderBy(r => r.ExpiresAt)
            .Take(batchSize)
            .ExecuteDeleteAsync(cancellationToken);
}
