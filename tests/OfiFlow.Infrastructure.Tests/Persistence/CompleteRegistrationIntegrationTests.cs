using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Identity.Commands.CompleteRegistration;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Persistence;

/// <summary>
/// Completar el registro con el caso de uso real, el servicio real de pendientes y SQL Server real (spec 021, bloque 3). Los tests de Application usan una base en memoria: no pueden demostrar
/// que la cuenta, la empresa y el canje se confirman <b>juntos</b>, ni que dos peticiones a la vez con el mismo token dejan <b>una sola</b> cuenta. Eso lo deciden la versión de fila y el índice
/// único de la base de datos, no el código, y por eso se prueba aquí con dos contextos de verdad.
/// </summary>
public class CompleteRegistrationIntegrationTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private const string Password = "Clave-Elegida-2026!";
    private static readonly DateTime Start = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerContainerFixture _fixture = fixture;

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@example.com";

    /// <summary>Pide el registro como lo haría el endpoint (con el servicio real) y devuelve el token en claro que iría en el correo.</summary>
    private async Task<string> IssueAsync(string email, string company, string person, FakeTimeProvider time)
    {
        await using var db = _fixture.CreateDbContext(Guid.Empty);
        var issue = await new PendingRegistrationService(db, time).IssueAsync(email, company, person, "2026-11", CancellationToken.None);
        Assert.Equal(PendingRegistrationIssueOutcome.Issued, issue.Outcome);
        return issue.Token!;
    }

    /// <summary>Un caso de uso con un contexto SIN empresa activa, como el de una petición anónima. El decorador, si lo hay, se interpone entre el caso de uso y el servicio real.</summary>
    private CompleteRegistrationCommandHandler Handler(
        FakeTimeProvider time,
        out ApplicationDbContext db,
        Func<IPendingRegistrationService, IPendingRegistrationService>? decoratePending = null,
        Func<IIdentityService, IIdentityService>? decorateIdentity = null)
    {
        db = _fixture.CreateDbContext(Guid.Empty);
        IPendingRegistrationService pending = new PendingRegistrationService(db, time);
        IIdentityService identity = new IdentityService(db, NullLogger<IdentityService>.Instance);

        return new CompleteRegistrationCommandHandler(
            decoratePending?.Invoke(pending) ?? pending,
            decorateIdentity?.Invoke(identity) ?? identity,
            db,
            NullLogger<CompleteRegistrationCommandHandler>.Instance);
    }

    private async Task CompleteAsync(string token, FakeTimeProvider time, string password = Password)
    {
        var handler = Handler(time, out var db);
        await using (db)
        {
            await handler.Handle(new CompleteRegistrationCommand(token, password), CancellationToken.None);
        }
    }

    private async Task<List<Tenant>> TenantsNamedAsync(string company)
    {
        await using var db = _fixture.CreateDbContext(Guid.Empty);
        return await db.Tenants.AsNoTracking().Where(t => t.Name == company).ToListAsync();
    }

    private async Task<PendingRegistration> PendingOfAsync(string email)
    {
        await using var db = _fixture.CreateDbContext(Guid.Empty);
        var normalized = email.ToUpperInvariant();
        return await db.PendingRegistrations.AsNoTracking().SingleAsync(r => r.NormalizedEmail == normalized);
    }

    private async Task<bool> HasAccountAsync(string email)
    {
        await using var db = _fixture.CreateDbContext(Guid.Empty);
        var normalized = email.ToUpperInvariant();
        return await db.ApplicationUsers.AnyAsync(u => u.NormalizedEmail == normalized);
    }

    // ===== El camino feliz, con todo confirmado junto =====

    [Fact]
    public async Task Completing_CreatesTheAccount_TheCompany_TheUserAndTheOwner_AndConsumesTheRegistration()
    {
        var time = new FakeTimeProvider(Start);
        var email = UniqueEmail();
        var company = $"Empresa {Guid.NewGuid():N}";
        var token = await IssueAsync(email, company, "Ana Pérez", time);
        time.Advance(TimeSpan.FromHours(3));

        // Antes de abrir el enlace no existe ninguna cuenta (D1).
        Assert.False(await HasAccountAsync(email));
        Assert.Empty(await TenantsNamedAsync(company));

        await CompleteAsync(token, time);

        var tenant = Assert.Single(await TenantsNamedAsync(company));
        await using var check = _fixture.CreateDbContext(tenant.Id);
        var membership = await check.TenantUsers.SingleAsync();
        var user = await check.Users.SingleAsync(u => u.Id == membership.UserId);

        Assert.Equal(TenantRole.Owner, membership.Role);
        Assert.Equal("Ana Pérez", user.Name);
        Assert.Equal(email, user.ContactEmail.Value);
        Assert.Equal("2026-11", user.TermsVersion);
        Assert.Equal(Start, user.TermsAcceptedAt); // la hora de ACEPTAR es la de pedirlo, no la de abrir el enlace
        Assert.True(await HasAccountAsync(email));
        Assert.Equal(time.GetUtcNow().UtcDateTime, (await PendingOfAsync(email)).UsedAt);
    }

    [Fact]
    public async Task ThePersonCanThenLogInWithThePasswordTheyChoseNow_AndOnlyWithThat()
    {
        var time = new FakeTimeProvider(Start);
        var email = UniqueEmail();
        var token = await IssueAsync(email, $"Empresa {Guid.NewGuid():N}", "Ana", time);

        await CompleteAsync(token, time, "Mi-clave-elegida-1");

        await using var db = _fixture.CreateDbContext(Guid.Empty);
        var identity = new IdentityService(db, NullLogger<IdentityService>.Instance);
        Assert.NotNull(await identity.ValidateCredentialsWithoutLockoutAsync(email, "Mi-clave-elegida-1", CancellationToken.None));
        Assert.Null(await identity.ValidateCredentialsWithoutLockoutAsync(email, Password, CancellationToken.None));
    }

    // ===== Un token que no sirve: no nace nada =====

    [Fact]
    public async Task AnInventedToken_CreatesNothing()
    {
        var time = new FakeTimeProvider(Start);
        var before = await CountTenantsAsync();

        var error = await Assert.ThrowsAsync<DomainException>(() => CompleteAsync(Application.Common.Security.OpaqueToken.Generate().Value, time));

        Assert.Equal(IdentityErrors.RegistrationInvalid, error.Code);
        Assert.Equal(before, await CountTenantsAsync());
    }

    [Fact]
    public async Task AnExpiredToken_CreatesNothing_AndIsTheSameError()
    {
        var time = new FakeTimeProvider(Start);
        var email = UniqueEmail();
        var company = $"Empresa {Guid.NewGuid():N}";
        var token = await IssueAsync(email, company, "Ana", time);
        time.Advance(PendingRegistration.Lifetime + TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<DomainException>(() => CompleteAsync(token, time));

        Assert.Equal(IdentityErrors.RegistrationInvalid, error.Code);
        Assert.Empty(await TenantsNamedAsync(company));
        Assert.False(await HasAccountAsync(email));
    }

    [Fact]
    public async Task ATokenUsedOnce_DoesNotCreateASecondAccount()
    {
        var time = new FakeTimeProvider(Start);
        var email = UniqueEmail();
        var company = $"Empresa {Guid.NewGuid():N}";
        var token = await IssueAsync(email, company, "Ana", time);
        await CompleteAsync(token, time);

        var error = await Assert.ThrowsAsync<DomainException>(() => CompleteAsync(token, time, "Otra-clave-distinta-9"));

        Assert.Equal(IdentityErrors.RegistrationInvalid, error.Code);
        Assert.Single(await TenantsNamedAsync(company));
    }

    [Fact]
    public async Task ARequestReplacedByANewOne_OnlyTheLastLinkWorks()
    {
        var time = new FakeTimeProvider(Start);
        var email = UniqueEmail();
        var company = $"Empresa {Guid.NewGuid():N}";
        var first = await IssueAsync(email, company, "Ana", time);
        time.Advance(TimeSpan.FromMinutes(5));
        var second = await IssueAsync(email, company, "Ana", time);

        await Assert.ThrowsAsync<DomainException>(() => CompleteAsync(first, time));
        Assert.Empty(await TenantsNamedAsync(company));

        await CompleteAsync(second, time);
        Assert.Single(await TenantsNamedAsync(company));
    }

    [Fact]
    public async Task IfTheEmailGotAnAccountSinceItWasAsked_NothingIsCreated_AndTheLinkIsNotSpent()
    {
        var time = new FakeTimeProvider(Start);
        var email = UniqueEmail();
        var company = $"Empresa {Guid.NewGuid():N}";
        var token = await IssueAsync(email, company, "Ana", time);

        // Alguien creó la cuenta por otra vía (una invitación, por ejemplo) después de pedir el registro.
        await using (var other = _fixture.CreateDbContext(Guid.Empty))
        {
            other.ApplicationUsers.Add(new ApplicationUser(Guid.NewGuid(), email));
            await other.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<DomainException>(() => CompleteAsync(token, time));

        Assert.Equal(IdentityErrors.RegistrationInvalid, error.Code);
        Assert.Empty(await TenantsNamedAsync(company));
        Assert.Null((await PendingOfAsync(email)).UsedAt); // no se gastó: nada se confirmó
    }

    // ===== Todo o nada: un fallo al confirmar no deja NADA (ni la empresa, ni la cuenta, ni el pendiente gastado) =====

    [Fact]
    public async Task IfAnAccountSlipsInBetweenTheCheckAndTheSave_NothingIsLeftBehind_AndItIsTheSameError()
    {
        var time = new FakeTimeProvider(Start);
        var email = UniqueEmail();
        var company = $"Empresa {Guid.NewGuid():N}";
        var token = await IssueAsync(email, company, "Ana", time);

        // La carrera más estrecha: la cuenta se cuela DESPUÉS de que el caso de uso comprobara que el email estaba libre y ANTES de que confirme. Solo el índice único de la base de datos puede
        // pararlo, y debe hacerlo sin dejar a medias la empresa, el usuario ni el canje del pendiente.
        var handler = Handler(time, out var db, decorateIdentity: inner => new SlipsAnAccountIn(inner, _fixture, email));
        await using (db)
        {
            var error = await Assert.ThrowsAsync<DomainException>(() => handler.Handle(new CompleteRegistrationCommand(token, Password), CancellationToken.None));
            Assert.Equal(IdentityErrors.RegistrationInvalid, error.Code);
        }

        Assert.Empty(await TenantsNamedAsync(company));
        Assert.Null((await PendingOfAsync(email)).UsedAt);
        await using var check = _fixture.CreateDbContext(Guid.Empty);
        Assert.Equal(1, await check.ApplicationUsers.CountAsync(u => u.NormalizedEmail == email.ToUpperInvariant())); // solo la que se coló
    }

    // ===== Una carrera de verdad: dos peticiones con el mismo token, una sola cuenta =====

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task TwoSimultaneousCompletions_OfTheSameToken_CreateExactlyOneAccount_AndTheLoserLeavesNothing(int round)
    {
        var time = new FakeTimeProvider(Start);
        var email = UniqueEmail();
        var company = $"Empresa carrera {round} {Guid.NewGuid():N}";
        var token = await IssueAsync(email, company, "Ana", time);

        // Las dos leen el pendiente como vigente y lo marcan como canjeado ANTES de que ninguna confirme (la barrera lo garantiza). Sin la versión de fila y el índice único,
        // las dos crearían una empresa.
        using var barrier = new Barrier(2);

        async Task<Exception?> TryAsync(string password)
        {
            var handler = Handler(time, out var db, decoratePending: inner => new WaitsForTheOther(inner, barrier));
            await using (db)
            {
                try
                {
                    await handler.Handle(new CompleteRegistrationCommand(token, password), CancellationToken.None);
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            }
        }

        var results = await Task.WhenAll(Task.Run(() => TryAsync("Clave-de-la-primera-1")), Task.Run(() => TryAsync("Clave-de-la-segunda-2")));

        Assert.Single(results, result => result is null); // exactamente una ganó
        var loser = Assert.IsType<DomainException>(Assert.Single(results, result => result is not null));
        Assert.Equal(IdentityErrors.RegistrationInvalid, loser.Code); // la otra recibe el error de siempre, nunca un 500

        // Y la perdedora no dejó NADA: una sola empresa, un solo usuario, una sola pertenencia y una sola cuenta de acceso.
        var tenant = Assert.Single(await TenantsNamedAsync(company));
        await using var check = _fixture.CreateDbContext(tenant.Id);
        Assert.Equal(1, await check.TenantUsers.CountAsync());
        Assert.Equal(1, await check.Users.CountAsync(u => u.ContactEmail == Email.Create(email)));
        await using var global = _fixture.CreateDbContext(Guid.Empty);
        Assert.Equal(1, await global.ApplicationUsers.CountAsync(u => u.NormalizedEmail == email.ToUpperInvariant()));
        Assert.NotNull((await PendingOfAsync(email)).UsedAt);
    }

    private async Task<int> CountTenantsAsync()
    {
        await using var db = _fixture.CreateDbContext(Guid.Empty);
        return await db.Tenants.CountAsync();
    }

    /// <summary>Canjea de verdad y espera a que la otra petición también haya canjeado, antes de seguir: así las dos han visto «vigente» antes de que ninguna confirme.</summary>
    private sealed class WaitsForTheOther(IPendingRegistrationService inner, Barrier barrier) : IPendingRegistrationService
    {
        public Task<PendingRegistrationIssue> IssueAsync(string email, string companyName, string userName, string termsVersion, CancellationToken cancellationToken) =>
            inner.IssueAsync(email, companyName, userName, termsVersion, cancellationToken);

        public async Task<PendingRegistrationConsumption> ConsumeAsync(string token, CancellationToken cancellationToken)
        {
            var result = await inner.ConsumeAsync(token, cancellationToken);
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(30), cancellationToken), "La otra petición no llegó a la barrera.");
            return result;
        }
    }

    /// <summary>Crea la cuenta de verdad y, justo después, se cuela otra cuenta con el mismo email desde otro contexto, ya confirmada.</summary>
    private sealed class SlipsAnAccountIn(IIdentityService inner, SqlServerContainerFixture fixture, string email) : IIdentityService
    {
        public async Task<IdentityCreationResult> CreateUserAsync(Guid id, string address, string password, CancellationToken cancellationToken)
        {
            var result = await inner.CreateUserAsync(id, address, password, cancellationToken);

            await using var other = fixture.CreateDbContext(Guid.Empty);
            other.ApplicationUsers.Add(new ApplicationUser(Guid.NewGuid(), email));
            await other.SaveChangesAsync(cancellationToken);

            return result;
        }

        public Task<bool> IsEmailRegisteredAsync(string address, CancellationToken cancellationToken) => inner.IsEmailRegisteredAsync(address, cancellationToken);

        public Task<Guid?> FindUserIdAsync(string address, CancellationToken cancellationToken) => inner.FindUserIdAsync(address, cancellationToken);

        public Task<PasswordAuthentication?> AuthenticateAsync(string address, string password, CancellationToken cancellationToken) => inner.AuthenticateAsync(address, password, cancellationToken);

        public Task<bool> ConfirmPasswordAsync(Guid userId, string password, CancellationToken cancellationToken) => inner.ConfirmPasswordAsync(userId, password, cancellationToken);

        public Task<string?> SetPasswordAsync(Guid userId, string newPassword, CancellationToken cancellationToken) => inner.SetPasswordAsync(userId, newPassword, cancellationToken);

        public Task DeleteUserAsync(Guid id, CancellationToken cancellationToken) => inner.DeleteUserAsync(id, cancellationToken);
    }
}
