using OfiFlow.Application.Common.Security;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Tenancy;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// Las rutas de invitaciones y miembros por HTTP (spec 009, bloque 6), con la API real y SQL Server real: que existen, que
/// exigen sesión (salvo aceptar), qué estado devuelven y que quien no puede recibe un 403 sin que nada cambie.
/// El ciclo completo y cada criterio de aceptación se amplían en el bloque 7.
/// </summary>
public class UserManagementEndpointsTests(MembersFixture fixture) : IClassFixture<MembersFixture>
{
    private sealed record Problem(int? Status, string? Title, string? Detail, string? Code);

    private static async Task<Problem> ReadProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Problem>())!;

    private static string NewEmail() => $"invitada-{Guid.NewGuid():N}@example.com";

    private Task<HttpResponseMessage> InviteAsync(HttpClient client, string email, TenantRole role) =>
        client.PostAsJsonAsync("/api/v1/invitations", new { email, role = (int)role });

    private async Task<InvitationCreatedDto> InviteOkAsync(string email, TenantRole role)
    {
        var response = await InviteAsync(fixture.OwnerClient, email, role);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<InvitationCreatedDto>())!;
    }

    private Task<HttpResponseMessage> AcceptAsync(string token, string name = "Ana Pérez", string? password = null) =>
        fixture.AcceptInvitationAsync(token, name, password ?? MembersFixture.NewPassword());

    // ===== Exigen sesión, salvo aceptar =====

    [Theory]
    [InlineData("POST", "/api/v1/invitations")]
    [InlineData("GET", "/api/v1/invitations")]
    [InlineData("DELETE", "/api/v1/invitations/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/v1/users")]
    [InlineData("PUT", "/api/v1/users/00000000-0000-0000-0000-000000000001/role")]
    [InlineData("DELETE", "/api/v1/users/00000000-0000-0000-0000-000000000001")]
    public async Task EveryManagementRoute_RequiresASession(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(new { email = "a@example.com", role = 3 });
        }

        var response = await fixture.Anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AcceptingAnInvitation_DoesNotRequireASession()
    {
        // Con una forma de token incorrecta responde 400 de validación, no 401: la ruta es anónima.
        var response = await AcceptAsync("no-es-un-token");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ===== Invitar, ver y revocar =====

    [Fact]
    public async Task AnInvitation_ReturnsTheTokenOnce_WithNoStoreCaching()
    {
        var response = await InviteAsync(fixture.OwnerClient, NewEmail(), TenantRole.Technician);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());

        var created = (await response.Content.ReadFromJsonAsync<InvitationCreatedDto>())!;
        Assert.True(OpaqueToken.IsWellFormed(created.Token));
        Assert.InRange(created.ExpiresAt, DateTime.UtcNow.AddDays(6), DateTime.UtcNow.AddDays(8));
    }

    [Fact]
    public async Task ThePendingList_ShowsTheInvitation_ButNeverItsToken()
    {
        var email = NewEmail();
        var created = await InviteOkAsync(email, TenantRole.Manager);

        var response = await fixture.OwnerClient.GetAsync("/api/v1/invitations");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(created.Id.ToString(), json);
        Assert.Contains(email, json);
        Assert.DoesNotContain(created.Token, json);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hash", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARevokedInvitation_DisappearsFromTheList_AndItsTokenNoLongerWorks()
    {
        var created = await InviteOkAsync(NewEmail(), TenantRole.Employee);

        var revoke = await fixture.OwnerClient.DeleteAsync($"/api/v1/invitations/{created.Id}");

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        var listed = (await fixture.OwnerClient.GetFromJsonAsync<List<InvitationDto>>("/api/v1/invitations"))!;
        Assert.DoesNotContain(listed, i => i.Id == created.Id);

        var accept = await AcceptAsync(created.Token);
        Assert.Equal(HttpStatusCode.BadRequest, accept.StatusCode);
        Assert.Equal("invitation.invalid", (await ReadProblemAsync(accept)).Code);
    }

    [Fact]
    public async Task RevokingAnUnknownInvitation_Is404_AndRevokingTwiceIs400()
    {
        var unknown = await fixture.OwnerClient.DeleteAsync($"/api/v1/invitations/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("invitation.not_found", (await ReadProblemAsync(unknown)).Code);

        var created = await InviteOkAsync(NewEmail(), TenantRole.Employee);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.OwnerClient.DeleteAsync($"/api/v1/invitations/{created.Id}")).StatusCode);

        var again = await fixture.OwnerClient.DeleteAsync($"/api/v1/invitations/{created.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("invitation.invalid", (await ReadProblemAsync(again)).Code);
    }

    [Fact]
    public async Task InvitingWithBadData_IsRefusedWithItsCode()
    {
        var member = await fixture.SeedMemberAsync(TenantRole.Employee);
        var duplicate = NewEmail();
        await InviteOkAsync(duplicate, TenantRole.Technician);

        var owner = await InviteAsync(fixture.OwnerClient, NewEmail(), TenantRole.Owner);
        var invalidRole = await fixture.OwnerClient.PostAsJsonAsync("/api/v1/invitations", new { email = NewEmail(), role = 999 });
        var invalidEmail = await InviteAsync(fixture.OwnerClient, "sin-arroba", TenantRole.Technician);
        var hasAccount = await InviteAsync(fixture.OwnerClient, member.Email, TenantRole.Technician);
        var pending = await InviteAsync(fixture.OwnerClient, duplicate, TenantRole.Manager);

        Assert.Equal(HttpStatusCode.Forbidden, owner.StatusCode);
        Assert.Equal("tenant_user.role_not_manageable", (await ReadProblemAsync(owner)).Code);
        Assert.Equal(HttpStatusCode.BadRequest, invalidRole.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidEmail.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, hasAccount.StatusCode);
        Assert.Equal("user.email_already_registered", (await ReadProblemAsync(hasAccount)).Code);
        Assert.Equal(HttpStatusCode.BadRequest, pending.StatusCode);
        Assert.Equal("invitation.already_pending", (await ReadProblemAsync(pending)).Code);
    }

    // ===== Quién puede cada ruta (códigos de estado) =====

    public static TheoryData<string, TenantRole, bool> Matrix()
    {
        // Para cada operación, quién puede (X) y quién no (-) entre los roles que no son Owner. El Owner puede todo.
        string[] operations = ["users_list", "invite", "invitations_list", "revoke", "change_role", "remove"];
        var allowed = new Dictionary<string, TenantRole[]>
        {
            ["users_list"] = [TenantRole.Admin, TenantRole.Manager],
            ["invite"] = [TenantRole.Admin],
            ["invitations_list"] = [TenantRole.Admin],
            ["revoke"] = [TenantRole.Admin],
            ["change_role"] = [TenantRole.Admin],
            ["remove"] = [TenantRole.Admin]
        };

        var data = new TheoryData<string, TenantRole, bool>();
        foreach (var operation in operations)
        {
            foreach (var role in new[] { TenantRole.Admin, TenantRole.Manager, TenantRole.Employee, TenantRole.Technician })
            {
                data.Add(operation, role, allowed[operation].Contains(role));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task EachRoute_IsAllowedOrForbidden_ByRole(string operation, TenantRole role, bool allowed)
    {
        var actor = await fixture.SeedMemberAsync(role);
        var target = await fixture.SeedMemberAsync(TenantRole.Employee); // sobre quien actúan cambiar rol y quitar
        // Solo "revoke" necesita una invitación: cada empresa tiene un máximo de pendientes, y crear una por caso lo agotaba.
        var invitation = operation == "revoke" ? await InviteOkAsync(NewEmail(), TenantRole.Employee) : null;

        var response = operation switch
        {
            "users_list" => await actor.Client.GetAsync("/api/v1/users"),
            "invite" => await InviteAsync(actor.Client, NewEmail(), TenantRole.Employee),
            "invitations_list" => await actor.Client.GetAsync("/api/v1/invitations"),
            "revoke" => await actor.Client.DeleteAsync($"/api/v1/invitations/{invitation!.Id}"),
            "change_role" => await actor.Client.PutAsJsonAsync($"/api/v1/users/{target.TenantUserId}/role", new { role = (int)TenantRole.Technician }),
            "remove" => await actor.Client.DeleteAsync($"/api/v1/users/{target.TenantUserId}"),
            _ => throw new InvalidOperationException(operation)
        };

        if (allowed)
        {
            Assert.True(response.IsSuccessStatusCode, $"{role} debería poder {operation} y recibió {(int)response.StatusCode}.");
            return;
        }

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.forbidden", (await ReadProblemAsync(response)).Code);

        // Y nada cambió: el miembro sigue ahí con su rol, y la invitación sigue pendiente.
        Assert.Equal(1, await fixture.MembershipCountAsync(target.TenantUserId));
        Assert.Equal(TenantRole.Employee, await fixture.RoleOfAsync(target.TenantUserId));
        if (invitation is not null)
        {
            var pending = (await fixture.OwnerClient.GetFromJsonAsync<List<InvitationDto>>("/api/v1/invitations"))!;
            Assert.Contains(pending, i => i.Id == invitation.Id);
        }
    }

    // ===== La jerarquía, por HTTP =====

    [Fact]
    public async Task TheHierarchy_AppliesOverHttp()
    {
        var admin = await fixture.SeedMemberAsync(TenantRole.Admin);
        var otherAdmin = await fixture.SeedMemberAsync(TenantRole.Admin);
        var manager = await fixture.SeedMemberAsync(TenantRole.Manager);

        var inviteAdmin = await InviteAsync(admin.Client, NewEmail(), TenantRole.Admin);
        var changeAdmin = await admin.Client.PutAsJsonAsync($"/api/v1/users/{otherAdmin.TenantUserId}/role", new { role = (int)TenantRole.Manager });
        var promote = await admin.Client.PutAsJsonAsync($"/api/v1/users/{manager.TenantUserId}/role", new { role = (int)TenantRole.Admin });
        var removeAdmin = await admin.Client.DeleteAsync($"/api/v1/users/{otherAdmin.TenantUserId}");
        var changeSelf = await admin.Client.PutAsJsonAsync($"/api/v1/users/{admin.TenantUserId}/role", new { role = (int)TenantRole.Manager });
        var removeSelf = await admin.Client.DeleteAsync($"/api/v1/users/{admin.TenantUserId}");

        Assert.All(new[] { inviteAdmin, changeAdmin, promote, removeAdmin }, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.Equal("tenant_user.role_not_manageable", (await ReadProblemAsync(changeAdmin)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, changeSelf.StatusCode);
        Assert.Equal("tenant_user.cannot_manage_self", (await ReadProblemAsync(changeSelf)).Code);
        Assert.Equal("tenant_user.cannot_manage_self", (await ReadProblemAsync(removeSelf)).Code);

        // Nada cambió.
        Assert.Equal(TenantRole.Admin, await fixture.RoleOfAsync(otherAdmin.TenantUserId));
        Assert.Equal(TenantRole.Manager, await fixture.RoleOfAsync(manager.TenantUserId));
        Assert.Equal(1, await fixture.MembershipCountAsync(otherAdmin.TenantUserId));
    }

    [Fact]
    public async Task NobodyCanRemoveOrChangeTheOwner_NotEvenTheOwnerThemselves()
    {
        var admin = await fixture.SeedMemberAsync(TenantRole.Admin);
        var ownerMembership = (await fixture.OwnerClient.GetFromJsonAsync<List<TenantUserDto>>("/api/v1/users"))!
            .Single(m => m.IsCurrentUser);

        var adminRemoves = await admin.Client.DeleteAsync($"/api/v1/users/{ownerMembership.Id}");
        var adminChanges = await admin.Client.PutAsJsonAsync($"/api/v1/users/{ownerMembership.Id}/role", new { role = (int)TenantRole.Employee });
        var ownerRemovesSelf = await fixture.OwnerClient.DeleteAsync($"/api/v1/users/{ownerMembership.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, adminRemoves.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, adminChanges.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, ownerRemovesSelf.StatusCode);
        Assert.Equal(TenantRole.Owner, await fixture.RoleOfAsync(ownerMembership.Id));
    }

    // ===== Cambiar el rol y quitar: efecto real =====

    [Fact]
    public async Task AChangedRole_IsPersisted_AndShownInTheList()
    {
        var target = await fixture.SeedMemberAsync(TenantRole.Employee);

        var response = await fixture.OwnerClient.PutAsJsonAsync($"/api/v1/users/{target.TenantUserId}/role", new { role = (int)TenantRole.Manager });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(TenantRole.Manager, await fixture.RoleOfAsync(target.TenantUserId));
        var members = (await fixture.OwnerClient.GetFromJsonAsync<List<TenantUserDto>>("/api/v1/users"))!;
        Assert.Equal("Manager", members.Single(m => m.Id == target.TenantUserId).Role);
    }

    [Fact]
    public async Task ARemovedMember_Disappears_AndTheirStillValidTokenStopsWorking()
    {
        var target = await fixture.SeedMemberAsync(TenantRole.Technician);
        Assert.Equal(HttpStatusCode.OK, (await target.Client.GetAsync("/api/v1/customers")).StatusCode); // control

        var response = await fixture.OwnerClient.DeleteAsync($"/api/v1/users/{target.TenantUserId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await fixture.MembershipCountAsync(target.TenantUserId));
        var members = (await fixture.OwnerClient.GetFromJsonAsync<List<TenantUserDto>>("/api/v1/users"))!;
        Assert.DoesNotContain(members, m => m.Id == target.TenantUserId);

        // El mismo token, sin caducar, ya no sirve. Hasta la spec 015 lo detectaba la revalidación de la pertenencia (403); ahora la baja
        // borra sus refresh tokens y el token se rechaza antes de la autorización (401).
        Assert.Equal(HttpStatusCode.Unauthorized, (await target.Client.GetAsync("/api/v1/customers")).StatusCode);
    }

    [Fact]
    public async Task ChangingOrRemovingAnUnknownMember_Is404()
    {
        var change = await fixture.OwnerClient.PutAsJsonAsync($"/api/v1/users/{Guid.NewGuid()}/role", new { role = (int)TenantRole.Manager });
        var remove = await fixture.OwnerClient.DeleteAsync($"/api/v1/users/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, change.StatusCode);
        Assert.Equal("tenant_user.not_found", (await ReadProblemAsync(change)).Code);
        Assert.Equal(HttpStatusCode.NotFound, remove.StatusCode);
    }

    [Fact]
    public async Task TheMemberList_NeverExposesTheAccountId()
    {
        var json = await fixture.OwnerClient.GetStringAsync("/api/v1/users");
        using var document = JsonDocument.Parse(json);

        var first = document.RootElement.EnumerateArray().First();
        var names = first.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToList();

        Assert.DoesNotContain("userid", names);
        Assert.Contains("id", names);
        Assert.Contains("iscurrentuser", names);
    }

    // ===== Aceptar: el ciclo completo por HTTP =====

    [Fact]
    public async Task APersonAcceptsTheInvitation_LogsInWithTheirOwnPassword_AndAppearsInTheList()
    {
        var email = NewEmail();
        var password = MembersFixture.NewPassword();
        var created = await InviteOkAsync(email, TenantRole.Technician);

        var accept = await AcceptAsync(created.Token, "Ana Pérez", password);

        Assert.Equal(HttpStatusCode.NoContent, accept.StatusCode);
        Assert.Empty(await accept.Content.ReadAsByteArrayAsync()); // sin tokens de sesión ni nada más

        // Entra con la contraseña que ELLA eligió, y su token trae el rol de la invitación y la empresa de quien invitó.
        var login = await fixture.Anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = (await login.Content.ReadFromJsonAsync<AuthResultDto>())!;
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(tokens.AccessToken);
        Assert.Equal("Technician", jwt.Claims.Single(c => c.Type == "role").Value);
        Assert.Equal(fixture.TenantId.ToString(), jwt.Claims.Single(c => c.Type == "tenant_id").Value);

        var members = (await fixture.OwnerClient.GetFromJsonAsync<List<TenantUserDto>>("/api/v1/users"))!;
        var ana = members.Single(m => m.Email == email);
        Assert.Equal("Ana Pérez", ana.Name);
        Assert.Equal("Technician", ana.Role);
    }

    [Fact]
    public async Task AnAlreadyUsedToken_AndAnUnknownOne_GetTheSameResponse()
    {
        var created = await InviteOkAsync(NewEmail(), TenantRole.Employee);
        Assert.Equal(HttpStatusCode.NoContent, (await AcceptAsync(created.Token)).StatusCode);

        var used = await AcceptAsync(created.Token);
        var unknown = await AcceptAsync(OpaqueToken.Generate().Value);

        // Quien lo intenta no aprende cuál de los dos es: mismo estado, mismo código, mismo texto.
        Assert.Equal(HttpStatusCode.BadRequest, used.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var (usedProblem, unknownProblem) = (await ReadProblemAsync(used), await ReadProblemAsync(unknown));
        Assert.Equal("invitation.invalid", usedProblem.Code);
        Assert.Equal((usedProblem.Status, usedProblem.Title, usedProblem.Detail, usedProblem.Code),
            (unknownProblem.Status, unknownProblem.Title, unknownProblem.Detail, unknownProblem.Code));
    }
}
