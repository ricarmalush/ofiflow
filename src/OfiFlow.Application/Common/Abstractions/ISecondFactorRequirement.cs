using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Common.Abstractions;

/// <summary>
/// ¿Exige este rol el segundo factor? (spec 022 R1.) Es el punto por el que <see cref="ICurrentUser"/> consulta <b>la regla</b>: la implementación real
/// (<c>SecondFactorPolicy</c>, en Domain) se registra siempre y es la única que existe en producción. Existe como interfaz para que los tests de la API, que casi todos
/// usan un <c>Owner</c> o un <c>Admin</c> que <b>no tratan de esto</b>, puedan funcionar sin tener que activar el factor en cada fixture, y para que **las clases que sí prueban la
/// puerta** la activen de verdad. Un test de arquitectura impide que ningún código de <c>src</c> registre otra implementación.
/// </summary>
public interface ISecondFactorRequirement
{
    bool IsRequiredFor(TenantRole role);
}
