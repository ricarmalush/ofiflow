using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Api.Tests.Common;

/// <summary>
/// «Nadie tiene que tener el segundo factor»: la sustitución de <see cref="ISecondFactorRequirement"/> que usan, <b>por defecto</b>, los tests de la API (spec 022).
/// Casi todos usan un Owner o un Admin que <b>no tratan de esto</b> (clientes, trabajos, equipo, sesiones…); sin esta sustitución habría que activar el factor en cada fixture y
/// reescribir cientos de tests por una regla que ninguno prueba. Las clases que <b>sí</b> prueban la puerta la activan de verdad (<see cref="SqlServerApiFactory.EnforceSecondFactor"/>).
/// Es solo de tests: un test de arquitectura impide que ningún código de <c>src</c> registre otra implementación que la real.
/// </summary>
public sealed class NoSecondFactorRequirement : ISecondFactorRequirement
{
    public static NoSecondFactorRequirement Instance { get; } = new();

    public bool IsRequiredFor(TenantRole role) => false;
}
