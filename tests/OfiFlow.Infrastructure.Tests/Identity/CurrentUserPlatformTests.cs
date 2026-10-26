using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OfiFlow.Application.Identity;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// La revalidación del operador de la plataforma en cada petición (spec 011 R7 y R10), contra SQL Server real: lo que decide es
/// la base de datos y no el token, un token de empresa nunca es una sesión de plataforma, y cuesta exactamente una consulta.
/// </summary>
public class CurrentUserPlatformTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    private static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerContainerFixture _fixture = fixture;

    private sealed class QueryCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Una «petición»: su propio contexto y su propio CurrentUser, con las claims de un token (de plataforma o no).</summary>
    private (CurrentUser User, QueryCounter Queries) Request(Guid? userId, string? realm)
    {
        var queries = new QueryCounter();
        var db = _fixture.CreateDbContext(Guid.NewGuid(), builder => builder.AddInterceptors(queries));

        var claims = new List<Claim>();
        if (userId is not null)
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Sub, userId.Value.ToString()));
        }

        if (realm is not null)
        {
            claims.Add(new Claim(OfiFlowClaimTypes.Realm, realm));
        }

        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) }
        };

        return (new CurrentUser(accessor, db), queries);
    }

    private async Task<Guid> SeedOperatorAsync(bool disabled = false)
    {
        var userId = Guid.NewGuid();

        await using var db = _fixture.CreateDbContext(Guid.NewGuid());
        await new PlatformOperators(db).AddAsync(userId, Now, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None); // DisableAsync lee de la base de datos: el alta tiene que estar guardada

        if (disabled)
        {
            await new PlatformOperators(db).DisableAsync(userId, Now, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return userId;
    }

    [Fact]
    public async Task AnActiveOperatorWithAPlatformSession_IsVerified_WithOneQuery()
    {
        var userId = await SeedOperatorAsync();
        var (user, queries) = Request(userId, OfiFlowClaimTypes.PlatformRealm);

        Assert.True(user.IsPlatformSession);
        Assert.True(await user.VerifyPlatformOperatorAsync(CancellationToken.None));
        Assert.Equal(1, queries.Count);
    }

    [Fact]
    public async Task TheVerification_IsDoneOncePerRequest()
    {
        var userId = await SeedOperatorAsync();
        var (user, queries) = Request(userId, OfiFlowClaimTypes.PlatformRealm);

        await user.VerifyPlatformOperatorAsync(CancellationToken.None);
        await user.VerifyPlatformOperatorAsync(CancellationToken.None);
        await user.VerifyPlatformOperatorAsync(CancellationToken.None);

        Assert.Equal(1, queries.Count);
    }

    [Fact]
    public async Task ADisabledOperatorWithAStillValidToken_IsNotVerified()
    {
        // R10: lo que decide es la base de datos. El token sigue siendo de plataforma y no ha caducado; la cuenta ya no es un operador.
        var userId = await SeedOperatorAsync(disabled: true);
        var (user, _) = Request(userId, OfiFlowClaimTypes.PlatformRealm);

        Assert.True(user.IsPlatformSession);
        Assert.False(await user.VerifyPlatformOperatorAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OperatorStatusIsReadFromTheDatabase_NotRememberedAcrossRequests()
    {
        var userId = await SeedOperatorAsync();

        var (before, _) = Request(userId, OfiFlowClaimTypes.PlatformRealm);
        Assert.True(await before.VerifyPlatformOperatorAsync(CancellationToken.None));

        await using (var db = _fixture.CreateDbContext(Guid.NewGuid()))
        {
            await new PlatformOperators(db).DisableAsync(userId, Now, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // La siguiente petición ya ve la baja, sin esperar a que caduque nada.
        var (after, _) = Request(userId, OfiFlowClaimTypes.PlatformRealm);
        Assert.False(await after.VerifyPlatformOperatorAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnAccountThatIsNotAnOperator_IsNotVerified()
    {
        var (user, _) = Request(Guid.NewGuid(), OfiFlowClaimTypes.PlatformRealm);

        Assert.False(await user.VerifyPlatformOperatorAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(null)] // una sesión de empresa: no lleva el claim del mundo
    [InlineData("tenant")]
    [InlineData("Platform")] // el valor exacto, sin mayúsculas
    [InlineData("platform ")]
    [InlineData("")]
    public async Task ATokenThatIsNotAPlatformSession_IsNeverVerified_EvenForAnActiveOperatorAccount(string? realm)
    {
        var userId = await SeedOperatorAsync();
        var (user, queries) = Request(userId, realm);

        Assert.False(user.IsPlatformSession);
        Assert.False(await user.VerifyPlatformOperatorAsync(CancellationToken.None));
        Assert.Equal(0, queries.Count); // ni siquiera se consulta: fail-closed
    }

    [Fact]
    public async Task APlatformTokenWithoutTheAccount_IsNeverVerified()
    {
        var (user, queries) = Request(userId: null, OfiFlowClaimTypes.PlatformRealm);

        Assert.False(await user.VerifyPlatformOperatorAsync(CancellationToken.None));
        Assert.Equal(0, queries.Count);
    }
}
