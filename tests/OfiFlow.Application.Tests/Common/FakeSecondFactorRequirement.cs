using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Common;

/// <summary>
/// La regla de «qué roles deben tener el segundo factor» (spec 022) con la lista que el test indique. No copia la política de Domain a propósito: así un test prueba que el caso de uso
/// <b>consulta la interfaz</b> (y no una copia de la regla), eligiendo una lista distinta de la real.
/// </summary>
public sealed class FakeSecondFactorRequirement(params TenantRole[] required) : ISecondFactorRequirement
{
    public bool IsRequiredFor(TenantRole role) => required.Contains(role);
}
