using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Api.Common;
using OfiFlow.Domain.Tenancy;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// El límite de invitaciones (spec 017, R14, D10): <b>20 por hora y por persona</b>, con la API real. Es por persona y no por IP: todos los clientes de estos tests comparten
/// una IP, así que si el límite fuera por IP, el Owner y el Admin se estorbarían, y el test de abajo lo detecta.
/// </summary>
public class InvitationThrottleEndToEndTests(MembersFixture fixture) : IClassFixture<MembersFixture>
{
    private static string NewEmail() => $"persona-{Guid.NewGuid():N}@example.com";

    private static Task<HttpResponseMessage> InviteAsync(HttpClient client, TenantRole role = TenantRole.Employee) =>
        client.PostAsJsonAsync("/api/v1/invitations", new { email = NewEmail(), role = (int)role });

    // Cada test usa su PROPIA empresa: con 20 invitaciones por test, una sola empresa llegaría al tope de 25 pendientes y el test fallaría por otra regla.
    private async Task<Guid> NewTenantAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var tenant = Tenant.Create($"Empresa {Guid.NewGuid():N}");
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    [Fact]
    public void TheLimit_IsTwentyAnHour_TheApprovedValue()
    {
        // Los demás tests usan la constante; este fija el valor aprobado (D10) para que no cambie sin que alguien lo decida.
        Assert.Equal(20, InvitationThrottle.PermitLimit);
        Assert.Equal(TimeSpan.FromHours(1), InvitationThrottle.Window);
    }

    [Fact]
    public async Task OnePersonCanInviteTwentyTimesAnHour_AndTheNextOneIsRefused_WithTheUsualAnswer()
    {
        var tenantId = await NewTenantAsync();
        var admin = await fixture.SeedMemberAsync(TenantRole.Admin, tenantId);

        for (var i = 0; i < InvitationThrottle.PermitLimit; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await InviteAsync(admin.Client)).StatusCode);
        }

        var refused = await InviteAsync(admin.Client);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(refused.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.True(int.Parse(retryAfter.Single()) > 0);

        // Es la misma respuesta de siempre ante un 429: el cliente no tiene que saber de qué límite viene.
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rate_limit.exceeded", problem.GetProperty("code").GetString());
        Assert.Equal(429, problem.GetProperty("status").GetInt32());

        // Queda registrado como cualquier otro límite superado (evento 1101).
        Assert.Contains(fixture.Factory.Logs.Entries, e => e.EventId == 1101 && e.Message.Contains("/api/v1/invitations"));
    }

    [Fact]
    public async Task TheLimitIsPerPerson_AnotherPersonOfTheSameCompanyAndIp_IsNotAffected()
    {
        var tenantId = await NewTenantAsync();
        var admin = await fixture.SeedMemberAsync(TenantRole.Admin, tenantId);
        for (var i = 0; i < InvitationThrottle.PermitLimit; i++)
        {
            await InviteAsync(admin.Client);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await InviteAsync(admin.Client)).StatusCode); // control positivo: el Admin sí está limitado

        var other = await fixture.SeedMemberAsync(TenantRole.Admin, tenantId);
        Assert.Equal(HttpStatusCode.Created, (await InviteAsync(other.Client)).StatusCode);
    }

    [Fact]
    public async Task ARefusedAttempt_DoesNotReachTheUseCase_SoNothingIsCreatedAndNothingIsSent()
    {
        var tenantId = await NewTenantAsync();
        var admin = await fixture.SeedMemberAsync(TenantRole.Admin, tenantId);
        for (var i = 0; i < InvitationThrottle.PermitLimit; i++)
        {
            await InviteAsync(admin.Client);
        }

        var email = NewEmail();
        var refused = await admin.Client.PostAsJsonAsync("/api/v1/invitations", new { email, role = (int)TenantRole.Employee });

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        var pending = await admin.Client.GetStringAsync("/api/v1/invitations");
        Assert.DoesNotContain(email, pending);
    }
}
