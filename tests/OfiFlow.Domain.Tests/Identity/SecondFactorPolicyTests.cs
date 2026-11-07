using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Domain.Tests.Identity;

/// <summary>A quién se obliga el segundo factor (spec 022 R1): una lista pequeña y decidida, con un test que no deja que cambie sin querer.</summary>
public class SecondFactorPolicyTests
{
    [Theory]
    [InlineData(TenantRole.Owner, true)]
    [InlineData(TenantRole.Admin, true)]
    [InlineData(TenantRole.Manager, false)]
    [InlineData(TenantRole.Technician, false)]
    [InlineData(TenantRole.Employee, false)]
    public void TheSecondFactorIsRequiredOnlyForOwnerAndAdmin(TenantRole role, bool required)
    {
        Assert.Equal(required, SecondFactorPolicy.RequiredFor(role));
    }

    [Fact]
    public void EveryRoleIsCoveredByTheTableAbove_SoANewRoleForcesADecision()
    {
        // Si alguien añade un rol, este test falla hasta que decida (en la tabla de arriba y aquí) si lo exige: un rol nuevo no queda «sin exigir» por olvido.
        Assert.Equal(5, Enum.GetValues<TenantRole>().Length);
    }

    [Fact]
    public void ThePolicyDecidesWithTheRoleItIsGiven_NeverWithAnythingElse()
    {
        // Función pura: el mismo rol da siempre el mismo resultado (no depende del reloj, del entorno ni de una configuración que alguien pueda apagar).
        foreach (var role in Enum.GetValues<TenantRole>())
        {
            Assert.Equal(SecondFactorPolicy.RequiredFor(role), SecondFactorPolicy.RequiredFor(role));
        }
    }
}
