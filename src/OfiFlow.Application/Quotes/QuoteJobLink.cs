using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Domain.Jobs;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Application.Quotes;

/// <summary>El enlace opcional de un presupuesto con un trabajo (spec 023 R1): el trabajo existe en la empresa y es del <b>mismo cliente</b>.</summary>
internal static class QuoteJobLink
{
    public static async Task EnsureMatchesCustomerAsync(IApplicationDbContext dbContext, Guid? jobId, Guid customerId, CancellationToken cancellationToken)
    {
        if (jobId is not { } id)
        {
            return;
        }

        // El filtro global por empresa hace que el trabajo de otra empresa sea un 404 indistinguible de uno inexistente.
        var jobCustomerId = await dbContext.Jobs
            .Where(j => j.Id == id)
            .Select(j => (Guid?)j.CustomerId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(JobErrors.NotFound, id);

        if (jobCustomerId != customerId)
        {
            throw new BusinessRuleException(QuoteErrors.JobCustomerMismatch);
        }
    }
}
