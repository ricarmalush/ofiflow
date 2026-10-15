using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Identity.Mfa;

/// <summary>Preparación común de los tests del segundo factor: una cuenta con contraseña, una empresa y los dobles.</summary>
public abstract class MfaTestBase
{
    protected const string Password = "Password123!";

    protected readonly Guid UserId = Guid.NewGuid();
    protected readonly Guid TenantId = Guid.NewGuid();
    protected readonly TestDbContext Db = TestDbContextFactory.Create();
    protected readonly FakeIdentityService Identity = new();
    protected readonly FakeMfaService Mfa = new();
    protected readonly FakeTokenService Tokens = new();

    protected MfaTestBase()
    {
        Identity.CreateUserAsync(UserId, "ana@example.com", Password, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Quien hace la petición: la cuenta de la prueba, con el rol indicado.</summary>
    protected FakeCurrentUser Me(TenantRole role = TenantRole.Technician) => new(role, UserId, TenantId);

    protected void MfaIsEnabled(int recoveryCodes = 10) => Mfa.Set(UserId, new MfaStatus(true, false, recoveryCodes));

    protected void SetupIsPending() => Mfa.Set(UserId, new MfaStatus(false, true, 0));
}
