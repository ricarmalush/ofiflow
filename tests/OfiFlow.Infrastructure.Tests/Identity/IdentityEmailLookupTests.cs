using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>"¿Ya hay una cuenta con este email?" (spec 009 R6): lo que decide si se puede invitar a alguien.</summary>
public class IdentityEmailLookupTests
{
    private static (ApplicationDbContext Db, IdentityService Service) CreateSut()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new ApplicationDbContext(options, new FixedTenantContext(Guid.NewGuid()));
        return (db, new IdentityService(db, NullLogger<IdentityService>.Instance));
    }

    [Fact]
    public async Task ARegisteredEmail_IsFound_WhateverTheCase()
    {
        var (db, service) = CreateSut();
        await service.CreateUserAsync(Guid.NewGuid(), "Ana@Example.com", "Password123!", CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        Assert.True(await service.IsEmailRegisteredAsync("Ana@Example.com", CancellationToken.None));
        Assert.True(await service.IsEmailRegisteredAsync("ANA@EXAMPLE.COM", CancellationToken.None));
        Assert.True(await service.IsEmailRegisteredAsync("ana@example.com", CancellationToken.None));
    }

    [Fact]
    public async Task AnEmailNobodyHas_IsNotFound()
    {
        var (db, service) = CreateSut();
        await service.CreateUserAsync(Guid.NewGuid(), "ana@example.com", "Password123!", CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        // Control positivo del anterior: "no encontrado" no se debe a que la búsqueda no funcione.
        Assert.False(await service.IsEmailRegisteredAsync("luis@example.com", CancellationToken.None));
    }

    [Fact]
    public async Task AfterDeletingTheAccount_TheEmailIsFreeAgain()
    {
        var (db, service) = CreateSut();
        var id = Guid.NewGuid();
        await service.CreateUserAsync(id, "ana@example.com", "Password123!", CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        await service.DeleteUserAsync(id, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        Assert.False(await service.IsEmailRegisteredAsync("ana@example.com", CancellationToken.None));
    }
}
