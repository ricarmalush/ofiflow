using OfiFlow.Domain.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Domain.Tests.Tenancy;

public class TenantTests
{
    [Fact]
    public void Create_WithValidName_Succeeds()
    {
        var tenant = Tenant.Create("Fontanería Pérez");

        Assert.Equal("Fontanería Pérez", tenant.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithoutName_Throws(string name)
    {
        var exception = Assert.Throws<DomainException>(() => Tenant.Create(name));

        Assert.Equal(TenancyErrors.TenantNameRequired, exception.Code);
    }

    // ---- Suspensión (spec 011 R12) ----

    private static readonly DateTime SuspensionTime = new(2026, 10, 22, 10, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void ANewTenant_IsActive()
    {
        var tenant = Tenant.Create("Fontanería Pérez");

        Assert.False(tenant.IsSuspended);
        Assert.Null(tenant.SuspendedAt);
    }

    [Fact]
    public void Suspend_AnActiveTenant_StoresTheTimeItWasGiven()
    {
        var tenant = Tenant.Create("Fontanería Pérez");

        tenant.Suspend(SuspensionTime);

        Assert.True(tenant.IsSuspended);
        // La hora es la que pone quien llama (el reloj del servidor): el dominio no mira el reloj de la máquina.
        Assert.Equal(SuspensionTime, tenant.SuspendedAt);
    }

    [Fact]
    public void Suspend_ASuspendedTenant_Throws_AndKeepsTheOriginalTime()
    {
        var tenant = Tenant.Create("Fontanería Pérez");
        tenant.Suspend(SuspensionTime);

        var exception = Assert.Throws<DomainException>(() => tenant.Suspend(SuspensionTime.AddHours(5)));

        Assert.Equal(TenancyErrors.TenantAlreadySuspended, exception.Code);
        // Un segundo intento no pisa cuándo se suspendió de verdad.
        Assert.Equal(SuspensionTime, tenant.SuspendedAt);
    }

    [Fact]
    public void Reactivate_ASuspendedTenant_MakesItActiveAgain()
    {
        var tenant = Tenant.Create("Fontanería Pérez");
        tenant.Suspend(SuspensionTime);

        tenant.Reactivate();

        Assert.False(tenant.IsSuspended);
        Assert.Null(tenant.SuspendedAt);
    }

    [Fact]
    public void Reactivate_AnActiveTenant_Throws()
    {
        var tenant = Tenant.Create("Fontanería Pérez");

        var exception = Assert.Throws<DomainException>(() => tenant.Reactivate());

        Assert.Equal(TenancyErrors.TenantNotSuspended, exception.Code);
    }

    [Fact]
    public void ATenant_CanBeSuspendedAgain_AfterBeingReactivated()
    {
        // Control positivo del ciclo completo: sin él, un Reactivate que no vaciara el estado pasaría desapercibido.
        var tenant = Tenant.Create("Fontanería Pérez");
        tenant.Suspend(SuspensionTime);
        tenant.Reactivate();

        tenant.Suspend(SuspensionTime.AddDays(1));

        Assert.Equal(SuspensionTime.AddDays(1), tenant.SuspendedAt);
    }

    [Fact]
    public void Suspending_DoesNotChangeTheName()
    {
        var tenant = Tenant.Create("Fontanería Pérez");

        tenant.Suspend(SuspensionTime);

        // Suspender no toca ningún dato de la empresa (spec 011 R12).
        Assert.Equal("Fontanería Pérez", tenant.Name);
    }
}
