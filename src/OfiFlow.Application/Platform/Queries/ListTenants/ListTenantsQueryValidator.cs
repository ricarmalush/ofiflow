using FluentValidation;

namespace OfiFlow.Application.Platform.Queries.ListTenants;

public sealed class ListTenantsQueryValidator : AbstractValidator<ListTenantsQuery>
{
    public ListTenantsQueryValidator()
    {
        RuleFor(x => x.Page).InclusiveBetween(1, ListTenantsQuery.MaxPage);

        // El tope vive aquí y no en el handler: una petición que lo supera es un error de validación (400), no una página recortada
        // en silencio (ADR-009 R3).
        RuleFor(x => x.PageSize).InclusiveBetween(1, ListTenantsQuery.MaxPageSize);
    }
}
