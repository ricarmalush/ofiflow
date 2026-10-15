using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// El primer paso del login cuando la cuenta tiene segundo factor (spec 010 R10, R17), contra SQL Server real: la
/// contraseña correcta avisa de que falta el segundo factor y <b>no reinicia el contador</b>. Si lo reiniciara, quien
/// conoce la contraseña podría reiniciarlo tras cada cuatro códigos fallados y adivinar el código sin límite.
/// </summary>
public class SecondFactorLoginTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private const string Password = "Password123!";
    private const string Wrong = "incorrecta";

    private static readonly DateTime Start = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerContainerFixture _fixture = fixture;

    private ApplicationDbContext Open() => _fixture.CreateDbContext(Guid.NewGuid());

    private async Task<(Guid Id, string Email)> SeedAccountAsync(bool withMfa = false, bool pendingOnly = false)
    {
        var id = Guid.NewGuid();
        var email = $"{Guid.NewGuid():N}@example.com";

        await using var db = Open();
        await new IdentityService(db, NullLogger<IdentityService>.Instance).CreateUserAsync(id, email, Password, CancellationToken.None);

        if (withMfa || pendingOnly)
        {
            var credential = MfaCredential.CreatePending(id, "cifrado-de-prueba");

            if (!pendingOnly)
            {
                credential.Confirm(Start);
            }

            db.MfaCredentials.Add(credential);
        }

        await db.SaveChangesAsync(CancellationToken.None);

        return (id, email);
    }

    private async Task<PasswordAuthentication?> LoginAsync(string email, string password, FakeTimeProvider clock)
    {
        await using var db = Open();
        var service = new IdentityService(db, NullLogger<IdentityService>.Instance, clock, new CountingPasswordChecker());

        return await service.AuthenticateAsync(email, password, CancellationToken.None);
    }

    private async Task<(int Failed, DateTimeOffset? LockedUntil)> StateAsync(Guid id)
    {
        await using var db = Open();
        var user = await db.ApplicationUsers.AsNoTracking().SingleAsync(u => u.Id == id);

        return (user.AccessFailedCount, user.LockoutEnd);
    }

    private async Task FailAsync(string email, int count, FakeTimeProvider clock)
    {
        for (var attempt = 0; attempt < count; attempt++)
        {
            Assert.Null(await LoginAsync(email, Wrong, clock));
        }
    }

    [Fact]
    public async Task AnAccountWithMfa_AfterTheCorrectPassword_SaysTheSecondFactorIsRequired()
    {
        var (id, email) = await SeedAccountAsync(withMfa: true);

        var result = await LoginAsync(email, Password, new FakeTimeProvider(Start));

        Assert.Equal(new PasswordAuthentication(id, SecondFactorRequired: true).UserId, result!.UserId);
        Assert.True(result.SecondFactorRequired);
    }

    [Fact]
    public async Task AnAccountWithoutMfa_AfterTheCorrectPassword_SaysNoSecondFactorIsNeeded()
    {
        var (id, email) = await SeedAccountAsync();

        var result = await LoginAsync(email, Password, new FakeTimeProvider(Start));

        Assert.Equal(id, result!.UserId);
        Assert.False(result.SecondFactorRequired);
    }

    [Fact]
    public async Task APendingSecret_NotYetConfirmed_DoesNotCountAsMfa()
    {
        // Hasta confirmar el primer código, el MFA no está activo: exigirlo dejaría fuera a quien se equivocó al escanear.
        var (_, email) = await SeedAccountAsync(pendingOnly: true);

        var result = await LoginAsync(email, Password, new FakeTimeProvider(Start));

        Assert.False(result!.SecondFactorRequired);
    }

    [Fact]
    public async Task TheCorrectPassword_OnAnMfaAccount_DoesNotResetTheCounter()
    {
        var (id, email) = await SeedAccountAsync(withMfa: true);
        var clock = new FakeTimeProvider(Start);

        await FailAsync(email, 3, clock);
        Assert.Equal(3, (await StateAsync(id)).Failed);

        var result = await LoginAsync(email, Password, clock);

        Assert.True(result!.SecondFactorRequired);
        Assert.Equal(3, (await StateAsync(id)).Failed); // la reserva se devolvió, y el contador NO se puso a cero
    }

    [Fact]
    public async Task TheCorrectPassword_OnAnAccountWithoutMfa_DoesResetTheCounter()
    {
        // Control del anterior: sin MFA, la contraseña correcta es todo el acceso y sí lo reinicia.
        var (id, email) = await SeedAccountAsync();
        var clock = new FakeTimeProvider(Start);

        await FailAsync(email, 3, clock);
        Assert.Equal(3, (await StateAsync(id)).Failed);

        Assert.NotNull(await LoginAsync(email, Password, clock));

        Assert.Equal(0, (await StateAsync(id)).Failed);
    }

    [Fact]
    public async Task WithFourPreviousFailures_TheCorrectPassword_OnAnMfaAccount_DoesNotLockTheAccount()
    {
        // El caso que cazó la prueba de extremo a extremo: con 4 fallos, el quinto intento reserva, llega a 5 y bloquea antes
        // de saber si la contraseña es buena. Siendo buena, esa reserva se devuelve y el bloqueo que provocó se levanta: quien
        // por fin acierta con su contraseña no puede quedarse fuera por un intento que no fue un fallo.
        var (id, email) = await SeedAccountAsync(withMfa: true);
        var clock = new FakeTimeProvider(Start);

        await FailAsync(email, 4, clock);
        var result = await LoginAsync(email, Password, clock);

        Assert.True(result!.SecondFactorRequired);
        Assert.Equal((4, (DateTimeOffset?)null), await StateAsync(id)); // sigue en 4 y SIN bloqueo
    }

    [Fact]
    public async Task AnAttackerWhoKnowsThePassword_CannotKeepResettingTheCounterToGuessCodesForever()
    {
        // El ataque que R17 impide: probar 4 códigos, poner la contraseña (que ya conoce) para reiniciar el contador y
        // seguir. Aquí los "códigos fallados" se simulan con contraseñas erróneas, que suman en el mismo contador.
        var (id, email) = await SeedAccountAsync(withMfa: true);
        var clock = new FakeTimeProvider(Start);

        await FailAsync(email, 4, clock);
        Assert.NotNull(await LoginAsync(email, Password, clock)); // la contraseña correcta intercalada...
        Assert.Equal(4, (await StateAsync(id)).Failed); // ...no reinició nada

        await FailAsync(email, 1, clock); // el quinto fallo, 4 + 1

        Assert.NotNull((await StateAsync(id)).LockedUntil); // la cuenta se bloquea: el ataque no avanza sin límite
    }

    [Fact]
    public async Task TheSecondFactorAttempts_AndThePasswordAttempts_ShareOneCounter()
    {
        var (id, email) = await SeedAccountAsync(withMfa: true);
        var clock = new FakeTimeProvider(Start);

        await FailAsync(email, 2, clock);

        Assert.Equal(2, (await StateAsync(id)).Failed);
    }
}
