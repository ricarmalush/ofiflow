using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;
using OfiFlow.Infrastructure.Tests.Common;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// La sesión del token (el claim <c>sid</c>, spec 015) tal como la lee <see cref="CurrentUser"/> (spec 016 R11): sirve para marcar cuál de las sesiones de la
/// lista es esta. Un dato ausente o roto es nulo, nunca una excepción.
/// </summary>
public class CurrentUserSessionTests
{
    private static CurrentUser For(params Claim[] claims)
    {
        var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new FixedTenantContext(Guid.NewGuid()));
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } };

        return new CurrentUser(accessor, db);
    }

    [Fact]
    public void ReadsTheSessionOfTheToken_TheSidClaim()
    {
        // El nombre «sid» es contrato con TokenService (spec 015).
        var sessionId = Guid.NewGuid();

        Assert.Equal(sessionId, For(new Claim("sid", sessionId.ToString())).SessionId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("esto-no-es-un-guid")]
    [InlineData("12345")]
    public void WithoutAValidSid_TheSessionIsNull_NotAnException(string raw) => Assert.Null(For(new Claim("sid", raw)).SessionId);

    [Fact]
    public void WithoutTheClaim_TheSessionIsNull() => Assert.Null(For().SessionId);

    [Fact]
    public void TheSession_IsNeverTakenFromAnyOtherClaim()
    {
        // Ni el «sub» ni el «tenant_id» son la sesión: un token sin «sid» no puede hacerse pasar por una sesión con otro Guid del token.
        var user = For(new Claim("sub", Guid.NewGuid().ToString()), new Claim("tenant_id", Guid.NewGuid().ToString()));

        Assert.Null(user.SessionId);
    }
}
