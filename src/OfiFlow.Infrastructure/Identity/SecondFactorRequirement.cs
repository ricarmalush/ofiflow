using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Infrastructure.Identity;

/// <summary>
/// La regla real de a quién se obliga el segundo factor (spec 022): la de Domain (<see cref="SecondFactorPolicy"/>), sin añadir nada. Es la única implementación que se
/// registra en producción; un test de arquitectura impide que ningún otro fichero de <c>src</c> registre otra.
/// </summary>
internal sealed class SecondFactorRequirement : ISecondFactorRequirement
{
    public bool IsRequiredFor(TenantRole role) => SecondFactorPolicy.RequiredFor(role);
}
