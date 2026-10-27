using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Identity.Mfa;
using OfiFlow.Application.Platform;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Persistence;

/// <summary>
/// El alta y la baja de un operador de la plataforma (spec 011, bloque 4) con los servicios REALES contra SQL Server (ADR-008).
/// Es donde se demuestra de verdad lo que con dobles solo se supone: que el alta es todo o nada, que la cuenta nace con el
/// segundo factor confirmado y con el primer código ya gastado, y que dos altas simultáneas con el mismo email no dejan
/// filas sueltas.
/// </summary>
public class PlatformOperatorAdministrationTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private const string Password = "una-clave-larga-123";

    private static readonly DateTime Start = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerContainerFixture _fixture = fixture;
    private readonly AesGcmSecretProtector _protector = new(RandomNumberGenerator.GetBytes(32));

    private ApplicationDbContext Open() => _fixture.CreateDbContext(Guid.NewGuid());

    private PlatformOperatorAdministration Service(ApplicationDbContext db, FakeTimeProvider clock) =>
        new(
            db,
            new IdentityService(db, NullLogger<IdentityService>.Instance),
            new MfaService(db, _protector, NullLogger<MfaService>.Instance, clock),
            new PlatformOperators(db),
            new PlatformAudit(db),
            clock,
            NullLogger<PlatformOperatorAdministration>.Instance);

    private static string Email() => $"{Guid.NewGuid():N}@ofiflow.example";

    private static string CodeFor(byte[] secret, FakeTimeProvider clock, int stepOffset = 0) =>
        Totp.Compute(secret, Totp.StepAt(clock.GetUtcNow()) + stepOffset);

    private async Task<(OperatorCreationResult Result, byte[] Secret)> CreateAsync(string email, FakeTimeProvider clock)
    {
        var secret = TotpSecret.Generate();

        await using var db = Open();
        var result = await Service(db, clock).CreateAsync(email, Password, secret, CodeFor(secret, clock), CancellationToken.None);

        return (result, secret);
    }

    /// <summary>Cuántas filas hay en cada tabla que toca el alta, para comprobar «ni una de más» o «ni una menos».</summary>
    private async Task<(int Accounts, int Credentials, int RecoveryCodes, int Operators, int Audit)> CountsAsync()
    {
        await using var db = Open();

        return (
            await db.ApplicationUsers.CountAsync(),
            await db.MfaCredentials.CountAsync(),
            await db.RecoveryCodes.CountAsync(),
            await db.PlatformOperators.CountAsync(),
            await db.PlatformAuditEntries.CountAsync());
    }

    // ===== Alta =====

    [Fact]
    public async Task Create_LeavesTheAccountTheConfirmedSecondFactorTheCodesTheOperatorAndTheAudit()
    {
        var clock = new FakeTimeProvider(Start);
        var email = Email();

        var (result, secret) = await CreateAsync(email, clock);

        Assert.Equal(OperatorCreationOutcome.Created, result.Outcome);

        await using var db = Open();
        Assert.True(await db.ApplicationUsers.AnyAsync(u => u.Id == result.UserId && u.NormalizedEmail == email.ToUpperInvariant()));

        // El segundo factor nace CONFIRMADO (nunca existe un operador sin él, R2), con el primer código ya gastado.
        var credential = await db.MfaCredentials.AsNoTracking().SingleAsync(c => c.UserId == result.UserId);
        Assert.True(credential.IsConfirmed);
        Assert.Equal(Totp.StepAt(clock.GetUtcNow()), credential.LastUsedTimeStep);
        Assert.Equal(AuthenticationPolicyCount, await db.RecoveryCodes.CountAsync(c => c.UserId == result.UserId && c.UsedAt == null));

        Assert.True(await new PlatformOperators(db).IsActiveOperatorAsync(result.UserId, CancellationToken.None));

        var audit = await db.PlatformAuditEntries.AsNoTracking().SingleAsync(a => a.OperatorUserId == result.UserId);
        Assert.Equal(PlatformAuditActions.OperatorCreated, audit.Action);
        Assert.Null(audit.TenantId);

        // R1: nunca es miembro de una empresa, y no tiene un «usuario de empresa» (el dominio de las empresas no la conoce).
        Assert.False(await db.TenantUsers.IgnoreQueryFilters().AnyAsync(tu => tu.UserId == result.UserId));
        Assert.False(await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == result.UserId));

        // El secreto no está en claro en la base de datos.
        Assert.DoesNotContain(Convert.ToBase64String(secret), credential.SecretCipher);
    }

    private const int AuthenticationPolicyCount = OfiFlow.Application.Identity.AuthenticationPolicy.RecoveryCodeCount;

    [Fact]
    public async Task TheFirstCode_IsAlreadySpent_ButTheNextOneAndARecoveryCodeWork()
    {
        var clock = new FakeTimeProvider(Start);
        var (result, secret) = await CreateAsync(Email(), clock);

        await using var db = Open();
        var mfa = new MfaService(db, _protector, NullLogger<MfaService>.Instance, clock);

        // El mismo código con el que se dio de alta no sirve otra vez: quien lo viera por encima del hombro no entra (R4).
        Assert.False((await mfa.VerifySecondFactorAsync(result.UserId, CodeFor(secret, clock), CancellationToken.None)).Valid);

        // Control positivo: el del paso siguiente sí, y un código de recuperación también (y se consume).
        Assert.True((await mfa.VerifySecondFactorAsync(result.UserId, CodeFor(secret, clock, stepOffset: 1), CancellationToken.None)).Valid);

        var recovery = await mfa.VerifySecondFactorAsync(result.UserId, result.RecoveryCodesInClear[0], CancellationToken.None);
        Assert.True(recovery.Valid);
        Assert.True(recovery.UsedRecoveryCode);
    }

    [Fact]
    public async Task EnrollConfirmed_OnAnAccountThatAlreadyHasASecondFactor_Throws_AndKeepsTheExistingOne()
    {
        // Última barrera del servicio: el camino «ya confirmado» es para cuentas recién creadas y no pisa un secreto existente.
        var clock = new FakeTimeProvider(Start);
        var userId = Guid.NewGuid();
        var original = TotpSecret.Generate();

        await using (var account = Open())
        {
            await new IdentityService(account, NullLogger<IdentityService>.Instance).CreateUserAsync(userId, Email(), Password, CancellationToken.None);
            await account.SaveChangesAsync(CancellationToken.None);
        }

        // La cuenta tiene que existir ya en la base de datos para pedir el secreto (BeginSetupAsync busca su email).
        await using (var pending = Open())
        {
            await new MfaService(pending, _protector, NullLogger<MfaService>.Instance, clock).BeginSetupAsync(userId, original, CancellationToken.None);
            await pending.SaveChangesAsync(CancellationToken.None);
        }

        await using var db = Open();
        var mfa = new MfaService(db, _protector, NullLogger<MfaService>.Instance, clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mfa.EnrollConfirmedAsync(userId, TotpSecret.Generate(), acceptedStep: 1, ["hash"], CancellationToken.None));

        // El pendiente sigue pendiente: no se confirmó nada ni se guardó ningún código de recuperación.
        var status = await mfa.GetStatusAsync(userId, CancellationToken.None);
        Assert.True(status.SetupPending);
        Assert.False(status.IsEnabled);
    }

    [Fact]
    public async Task Create_WithAWrongFirstCode_LeavesTheDatabaseUntouched()
    {
        var clock = new FakeTimeProvider(Start);
        var before = await CountsAsync();

        await using (var db = Open())
        {
            var result = await Service(db, clock).CreateAsync(Email(), Password, TotpSecret.Generate(), "000000", CancellationToken.None);
            Assert.Equal(OperatorCreationOutcome.InvalidCode, result.Outcome);
        }

        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    public async Task Create_WithAnEmailOfACompanyAccount_ChangesNothing_AndTheAccountKeepsBeingACompanyOne()
    {
        // R3 y R1: el email de un miembro de una empresa queda ocupado; el alta falla y la cuenta existente no se toca.
        var clock = new FakeTimeProvider(Start);
        var email = Email();
        var companyUserId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        await using (var seed = _fixture.CreateDbContext(tenantId))
        {
            Assert.True((await new IdentityService(seed, NullLogger<IdentityService>.Instance)
                .CreateUserAsync(companyUserId, email, "Password123!", CancellationToken.None)).Succeeded);
            seed.TenantUsers.Add(TenantUser.CreateOwner(tenantId, companyUserId));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var before = await CountsAsync();

        var (result, _) = await CreateAsync(email, clock);

        Assert.Equal(OperatorCreationOutcome.EmailAlreadyRegistered, result.Outcome);
        Assert.Equal(before, await CountsAsync());

        await using var db = Open();
        Assert.False(await db.MfaCredentials.AnyAsync(c => c.UserId == companyUserId));
        Assert.False(await db.PlatformOperators.AnyAsync(o => o.UserId == companyUserId));
    }

    [Fact]
    public async Task TwoSimultaneousCreations_WithTheSameEmail_LeaveExactlyOneOperator_AndNoStrayRows()
    {
        // La que pierde ya había preparado cuenta, segundo factor, 10 códigos, operador y auditoría cuando su guardado
        // falla por el índice único del email: si no fuera todo o nada, quedarían filas sueltas de la perdedora.
        var clock = new FakeTimeProvider(Start);
        var email = Email();
        var before = await CountsAsync();

        var results = await Task.WhenAll(Task.Run(() => CreateAsync(email, clock)), Task.Run(() => CreateAsync(email, clock)));

        var outcomes = results.Select(r => r.Result.Outcome).Order().ToList();
        Assert.Equal([OperatorCreationOutcome.Created, OperatorCreationOutcome.EmailAlreadyRegistered], outcomes);

        var after = await CountsAsync();
        Assert.Equal(before.Accounts + 1, after.Accounts);
        Assert.Equal(before.Credentials + 1, after.Credentials);
        Assert.Equal(before.RecoveryCodes + AuthenticationPolicyCount, after.RecoveryCodes);
        Assert.Equal(before.Operators + 1, after.Operators);
        Assert.Equal(before.Audit + 1, after.Audit);
    }

    // ===== Baja =====

    [Fact]
    public async Task Disable_MarksTheOperatorAsDisabled_AndWritesTheAudit()
    {
        var clock = new FakeTimeProvider(Start);
        var email = Email();
        var (created, _) = await CreateAsync(email, clock);
        clock.Advance(TimeSpan.FromHours(2));

        await using (var db = Open())
        {
            Assert.Equal(OperatorDisableOutcome.Disabled, await Service(db, clock).DisableAsync(email, CancellationToken.None));
        }

        await using var read = Open();
        var row = await read.PlatformOperators.AsNoTracking().SingleAsync(o => o.UserId == created.UserId);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, row.DisabledAt);
        Assert.False(await new PlatformOperators(read).IsActiveOperatorAsync(created.UserId, CancellationToken.None));

        var actions = await read.PlatformAuditEntries.AsNoTracking()
            .Where(a => a.OperatorUserId == created.UserId)
            .Select(a => a.Action)
            .ToListAsync();
        Assert.Contains(PlatformAuditActions.OperatorCreated, actions);
        Assert.Contains(PlatformAuditActions.OperatorDisabled, actions);
    }

    [Fact]
    public async Task Disable_TwiceOrForAnUnknownOrCompanyAccount_ChangesNothing()
    {
        var clock = new FakeTimeProvider(Start);
        var email = Email();
        await CreateAsync(email, clock);

        await using (var db = Open())
        {
            Assert.Equal(OperatorDisableOutcome.Disabled, await Service(db, clock).DisableAsync(email, CancellationToken.None));
        }

        var companyEmail = Email();
        var tenantId = Guid.NewGuid();
        await using (var seed = _fixture.CreateDbContext(tenantId))
        {
            var companyUserId = Guid.NewGuid();
            await new IdentityService(seed, NullLogger<IdentityService>.Instance).CreateUserAsync(companyUserId, companyEmail, "Password123!", CancellationToken.None);
            seed.TenantUsers.Add(TenantUser.CreateOwner(tenantId, companyUserId));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var before = await CountsAsync();

        foreach (var target in new[] { email, "nadie@ofiflow.example", companyEmail })
        {
            await using var db = Open();
            Assert.Equal(OperatorDisableOutcome.NotAnActiveOperator, await Service(db, clock).DisableAsync(target, CancellationToken.None));
        }

        Assert.Equal(before, await CountsAsync()); // sin una segunda fila de auditoría ni cambios en nadie
    }
}
