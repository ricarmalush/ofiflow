using MediatR;
using OfiFlow.Application.Common.Abstractions;

namespace OfiFlow.Application.Identity.Mfa.Queries.GetMfaStatus;

public sealed class GetMfaStatusQueryHandler(ICurrentUser currentUser, IMfaService mfaService)
    : IRequestHandler<GetMfaStatusQuery, MfaStatusDto>
{
    public async Task<MfaStatusDto> Handle(GetMfaStatusQuery request, CancellationToken cancellationToken)
    {
        // La cuenta sale del token (ya verificado), nunca de un campo de la petición: aquí no hay ningún Id que manipular.
        var status = await mfaService.GetStatusAsync(currentUser.UserId!.Value, cancellationToken);

        return new MfaStatusDto(status.IsEnabled, status.SetupPending, status.RecoveryCodesRemaining);
    }
}
