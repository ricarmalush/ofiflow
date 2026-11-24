using MediatR;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Application.Common.Persistence;

namespace OfiFlow.Application.Company.Queries.GetCompanyProfile;

public sealed class GetCompanyProfileQueryHandler(IApplicationDbContext dbContext) : IRequestHandler<GetCompanyProfileQuery, CompanyProfileDto>
{
    public async Task<CompanyProfileDto> Handle(GetCompanyProfileQuery request, CancellationToken cancellationToken)
    {
        // Solo lectura y sin seguimiento. El filtro global limita la búsqueda a la empresa del token: el perfil de otra no existe aquí (R8).
        var profile = await dbContext.CompanyProfiles.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

        if (profile is null)
        {
            return CompanyProfileDto.Empty;
        }

        return CompanyProfileDto.From(profile);
    }
}
