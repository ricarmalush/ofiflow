using System.Net;
using System.Net.Http.Json;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Jobs;
using OfiFlow.Application.Tenancy;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// Los criterios de aceptación de la spec 009 por HTTP, con la API real y SQL Server real (bloque 7): cada denegación con
/// su control positivo, y cada garantía de seguridad comprobada en la base de datos y en el log, no solo en la respuesta.
/// Las rutas y los códigos de estado básicos están en <see cref="UserManagementEndpointsTests"/>.
/// </summary>
public class UserManagementAcceptanceTests(MembersFixture fixture) : IClassFixture<MembersFixture>
{
    private sealed record Problem(int? Status, string? Title, string? Detail, string? Code);

    private static async Task<Problem> ReadProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Problem>())!;

    /// <summary>Lo que debe ser idéntico entre dos respuestas de error. El traceId cambia en cada petición y se deja fuera.</summary>
    private static (int? Status, string? Title, string? Detail, string? Code) Shape(Problem problem) =>
        (problem.Status, problem.Title, problem.Detail, problem.Code);

    private static string NewEmail(string prefix = "persona") => $"{prefix}-{Guid.NewGuid():N}@example.com";

    private Task<HttpResponseMessage> InviteAsync(HttpClient client, string email, TenantRole role) =>
        client.PostAsJsonAsync("/api/v1/invitations", new { email, role = (int)role });

    private async Task<InvitationCreatedDto> InviteOkAsync(string email, TenantRole role, HttpClient? client = null)
    {
        var response = await InviteAsync(client ?? fixture.OwnerClient, email, role);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<InvitationCreatedDto>())!;
    }

    private Task<HttpResponseMessage> AcceptAsync(string token, string name = "Persona de prueba", string? password = null) =>
        fixture.AcceptInvitationAsync(token, name, password ?? MembersFixture.NewPassword());

    private async Task<List<TenantUserDto>> MembersAsync(HttpClient? client = null) =>
        (await (client ?? fixture.OwnerClient).GetFromJsonAsync<List<TenantUserDto>>("/api/v1/users"))!;

    private async Task<List<InvitationDto>> PendingAsync(HttpClient? client = null) =>
        (await (client ?? fixture.OwnerClient).GetFromJsonAsync<List<InvitationDto>>("/api/v1/invitations"))!;

    private async Task RevokeAsync(Guid invitationId) =>
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.OwnerClient.DeleteAsync($"/api/v1/invitations/{invitationId}")).StatusCode);

    // ===== Quién puede invitar a quién, con su control positivo =====

    [Theory]
    [InlineData(TenantRole.Owner, TenantRole.Admin, true)]
    [InlineData(TenantRole.Owner, TenantRole.Manager, true)]
    [InlineData(TenantRole.Owner, TenantRole.Technician, true)]
    [InlineData(TenantRole.Owner, TenantRole.Employee, true)]
    [InlineData(TenantRole.Owner, TenantRole.Owner, false)]
    [InlineData(TenantRole.Admin, TenantRole.Manager, true)]
    [InlineData(TenantRole.Admin, TenantRole.Technician, true)]
    [InlineData(TenantRole.Admin, TenantRole.Employee, true)]
    [InlineData(TenantRole.Admin, TenantRole.Admin, false)]
    [InlineData(TenantRole.Admin, TenantRole.Owner, false)]
    public async Task WhoCanInviteWhom_OverHttp(TenantRole actorRole, TenantRole invitedRole, bool allowed)
    {
        var actor = actorRole == TenantRole.Owner ? null : await fixture.SeedMemberAsync(actorRole);
        var client = actor?.Client ?? fixture.OwnerClient;
        var email = NewEmail();

        var response = await InviteAsync(client, email, invitedRole);

        if (allowed)
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var created = (await response.Content.ReadFromJsonAsync<InvitationCreatedDto>())!;
            Assert.Contains(await PendingAsync(), i => i.Id == created.Id && i.Role == invitedRole.ToString());
            await RevokeAsync(created.Id); // no acumular pendientes: cada empresa tiene un máximo
        }
        else
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("tenant_user.role_not_manageable", (await ReadProblemAsync(response)).Code);
            // Y no se creó nada: ninguna invitación para ese email, y nunca una con el rol Owner.
            var pending = await PendingAsync();
            Assert.DoesNotContain(pending, i => i.Email == email);
            Assert.DoesNotContain(pending, i => i.Role == nameof(TenantRole.Owner));
        }
    }

    // ===== Aceptar: la misma respuesta para cuatro fallos distintos =====

    [Fact]
    public async Task UnknownExpiredRevokedAndUsedTokens_GetExactlyTheSameResponse_AndCreateNothing()
    {
        var unknown = InvitationToken.Generate().Value;

        var expired = await InviteOkAsync(NewEmail("caducada"), TenantRole.Employee);
        await fixture.ExpireInvitationAsync(expired.Id);

        var revoked = await InviteOkAsync(NewEmail("revocada"), TenantRole.Employee);
        await RevokeAsync(revoked.Id);

        var used = await InviteOkAsync(NewEmail("usada"), TenantRole.Employee);
        Assert.Equal(HttpStatusCode.NoContent, (await AcceptAsync(used.Token)).StatusCode);

        var membersBefore = (await MembersAsync()).Count;
        var shapes = new List<(int? Status, string? Title, string? Detail, string? Code)>();
        foreach (var token in new[] { unknown, expired.Token, revoked.Token, used.Token })
        {
            var response = await AcceptAsync(token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            shapes.Add(Shape(await ReadProblemAsync(response)));
        }

        // Quien lo intenta no aprende cuál de los cuatro casos es: estado, título, texto y código, idénticos.
        Assert.Single(shapes.Distinct());
        Assert.Equal("invitation.invalid", shapes[0].Code);
        Assert.Equal(membersBefore, (await MembersAsync()).Count); // los cuatro intentos no crearon nada
    }

    [Fact]
    public async Task TwoSimultaneousAcceptancesOfTheSameToken_CreateExactlyOneAccount()
    {
        var email = NewEmail("carrera");
        var created = await InviteOkAsync(email, TenantRole.Technician);

        var responses = await Task.WhenAll(
            Task.Run(() => AcceptAsync(created.Token, "Primera")),
            Task.Run(() => AcceptAsync(created.Token, "Segunda")));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.NoContent); // exactamente una gana
        var loser = Assert.Single(responses, r => r.StatusCode != HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.BadRequest, loser.StatusCode); // la otra recibe el 400 de "token que ya no sirve", no un 500
        Assert.Equal("invitation.invalid", (await ReadProblemAsync(loser)).Code);

        Assert.Single(await MembersAsync(), m => m.Email == email);
    }

    [Fact]
    public async Task InvalidNameOrPassword_AreRejected_WithoutConsumingTheToken()
    {
        var email = NewEmail("validacion");
        var created = await InviteOkAsync(email, TenantRole.Employee);

        var emptyName = await AcceptAsync(created.Token, name: "");
        var shortPassword = await AcceptAsync(created.Token, password: "corta");

        Assert.Equal(HttpStatusCode.BadRequest, emptyName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);
        Assert.Contains(await PendingAsync(), i => i.Id == created.Id); // sigue pendiente

        // Control positivo: con datos válidos, el MISMO token sí funciona.
        Assert.Equal(HttpStatusCode.NoContent, (await AcceptAsync(created.Token)).StatusCode);
    }

    [Fact]
    public async Task IfTheEmailGetsAnAccountMeanwhile_AcceptingFails_AndNothingIsCreated()
    {
        var email = NewEmail("ocupado");
        var created = await InviteOkAsync(email, TenantRole.Employee);
        await fixture.CreateAccountAsync(email); // alguien se registró con ese email mientras tanto

        var response = await AcceptAsync(created.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("user.email_already_registered", (await ReadProblemAsync(response)).Code);
        Assert.DoesNotContain(await MembersAsync(), m => m.Email == email);
        Assert.Contains(await PendingAsync(), i => i.Id == created.Id); // la invitación no se consumió
    }

    // ===== El token en claro no está en ningún sitio; ningún dato personal en los eventos =====

    [Fact]
    public async Task TheTokenInTheClear_IsNeverInTheDatabase_NorInAnyLog_AndTheEventsHoldNoPersonalData()
    {
        var email = NewEmail("secreto-visible");
        var name = $"Nombre-Distintivo-{Guid.NewGuid():N}";
        var password = $"Clave-Distintiva-{Guid.NewGuid():N}";

        var created = await InviteOkAsync(email, TenantRole.Technician);
        Assert.Equal(HttpStatusCode.NoContent, (await AcceptAsync(created.Token, name, password)).StatusCode);
        var member = (await MembersAsync()).Single(m => m.Email == email);

        // Base de datos: ninguna columna de la invitación contiene el token en claro (solo su hash).
        var row = await fixture.InvitationRowAsTextAsync(created.Id);
        Assert.DoesNotContain(created.Token, row);
        Assert.Contains(InvitationToken.Hash(created.Token), row);

        // Registro: ni el token ni la contraseña aparecen en NINGÚN log.
        var allLogs = fixture.Factory.Logs.Entries;
        Assert.DoesNotContain(allLogs, e => e.Message.Contains(created.Token));
        Assert.DoesNotContain(allLogs, e => e.Message.Contains(InvitationToken.Hash(created.Token)));
        Assert.DoesNotContain(allLogs, e => e.Message.Contains(password));

        // Eventos de seguridad: con los Ids, pero sin el email ni el nombre (control positivo: los eventos SÍ existen).
        int[] events = [1301, 1302, 1401, 1402, 1403, 1404, 1405, 1406];
        var securityLogs = allLogs.Where(e => events.Contains(e.EventId)).ToList();
        Assert.Contains(securityLogs, e => e.EventId == 1401 && e.Message.Contains(created.Id.ToString()));
        Assert.Contains(securityLogs, e => e.EventId == 1402 && e.Message.Contains(created.Id.ToString()));
        Assert.DoesNotContain(securityLogs, e => e.Message.Contains(email));
        Assert.DoesNotContain(securityLogs, e => e.Message.Contains(name));
        Assert.NotEqual(Guid.Empty, member.Id);
    }

    [Fact]
    public async Task AHierarchyRefusal_IsLoggedWithItsReason_ButNeverTheInvitedEmail()
    {
        var admin = await fixture.SeedMemberAsync(TenantRole.Admin);
        var email = NewEmail("rechazada-visible");
        var before = fixture.Factory.Logs.Entries.Count(e => e.EventId == 1301);

        Assert.Equal(HttpStatusCode.Forbidden, (await InviteAsync(admin.Client, email, TenantRole.Admin)).StatusCode);

        var denials = fixture.Factory.Logs.Entries.Where(e => e.EventId == 1301).Skip(before).ToList();
        var denial = Assert.Single(denials);
        Assert.Contains("RoleNotManageable", denial.Message);
        Assert.Contains(admin.UserId.ToString(), denial.Message);
        Assert.DoesNotContain(email, denial.Message);
    }

    // ===== Quitar: efecto completo, y el email queda libre =====

    [Fact]
    public async Task ARemovedMember_CannotRenewTheSessionNorLogIn_AndTheEmailCanBeInvitedAgain()
    {
        var email = NewEmail("baja");
        var password = MembersFixture.NewPassword();
        var created = await InviteOkAsync(email, TenantRole.Technician);
        Assert.Equal(HttpStatusCode.NoContent, (await AcceptAsync(created.Token, "Persona de baja", password)).StatusCode);

        var login = await fixture.Anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode); // control: antes de la baja entra
        var tokens = (await login.Content.ReadFromJsonAsync<AuthResultDto>())!;
        using var memberClient = fixture.ClientWithToken(tokens.AccessToken);
        var membership = (await MembersAsync()).Single(m => m.Email == email);

        Assert.Equal(HttpStatusCode.NoContent, (await fixture.OwnerClient.DeleteAsync($"/api/v1/users/{membership.Id}")).StatusCode);

        // Ni renueva la sesión, ni entra de nuevo, ni usa el token que aún no ha caducado.
        var refresh = await fixture.Anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken });
        var relogin = await fixture.Anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, relogin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.GetAsync("/api/v1/customers")).StatusCode);

        // Su cuenta y sus datos ya no existen: el email está libre y se puede volver a invitar a esa persona.
        Assert.False(await fixture.EmailHasAnAccountAsync(email));
        var again = await InviteOkAsync(email, TenantRole.Manager);
        Assert.Equal(HttpStatusCode.NoContent, (await AcceptAsync(again.Token, "Persona que vuelve", password)).StatusCode);
        Assert.Equal("Manager", (await MembersAsync()).Single(m => m.Email == email).Role);

        // El evento de la baja queda registrado, con el Id de la pertenencia y sin el email.
        Assert.Contains(fixture.Factory.Logs.Entries, e => e.EventId == 1406 && e.Message.Contains(membership.Id.ToString()));
    }

    [Fact]
    public async Task RemovingAMember_UnassignsTheirUnfinishedJobs_AndKeepsTheFinishedOnes()
    {
        var member = await fixture.SeedMemberAsync(TenantRole.Technician);
        var customer = await CreateAsync("/api/v1/customers", new { type = 0, name = $"Cliente {Guid.NewGuid():N}" });
        var openJob = await CreateAsync("/api/v1/jobs", new { customerId = customer, title = "Abierto", priority = 1 });
        var doneJob = await CreateAsync("/api/v1/jobs", new { customerId = customer, title = "Terminado", priority = 1 });

        foreach (var job in new[] { openJob, doneJob })
        {
            Assert.Equal(HttpStatusCode.NoContent,
                (await fixture.OwnerClient.PostAsJsonAsync($"/api/v1/jobs/{job}/assign", new { tenantUserId = member.TenantUserId })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await fixture.OwnerClient.PostAsync($"/api/v1/jobs/{doneJob}/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.OwnerClient.PostAsync($"/api/v1/jobs/{doneJob}/complete", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await fixture.OwnerClient.DeleteAsync($"/api/v1/users/{member.TenantUserId}")).StatusCode);

        var open = (await fixture.OwnerClient.GetFromJsonAsync<JobDto>($"/api/v1/jobs/{openJob}"))!;
        var done = (await fixture.OwnerClient.GetFromJsonAsync<JobDto>($"/api/v1/jobs/{doneJob}"))!;
        Assert.Null(open.AssignedTenantUserId); // el abierto queda sin asignar
        Assert.Equal("New", open.Status);
        Assert.Equal(member.TenantUserId, done.AssignedTenantUserId); // el terminado conserva la referencia como historial
        Assert.Equal("Completed", done.Status);
    }

    private async Task<Guid> CreateAsync(string url, object body)
    {
        var response = await fixture.OwnerClient.PostAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Created>())!.Id;
    }

    private sealed record Created(Guid Id);

    // ===== Cambiar el rol por la API: el token anterior ya no manda =====

    [Fact]
    public async Task ARoleChangedThroughTheApi_AppliesToTheNextRequest_WithTheOldToken()
    {
        var member = await fixture.SeedMemberAsync(TenantRole.Manager); // su token dice Manager
        var jobCustomer = await CreateAsync("/api/v1/customers", new { type = 0, name = $"Cliente {Guid.NewGuid():N}" });
        var job = await CreateAsync("/api/v1/jobs", new { customerId = jobCustomer, title = "Para asignar", priority = 1 });

        // Control: como Manager puede asignar trabajos.
        Assert.Equal(HttpStatusCode.NoContent,
            (await member.Client.PostAsJsonAsync($"/api/v1/jobs/{job}/assign", new { tenantUserId = member.TenantUserId })).StatusCode);

        // El Owner lo degrada POR LA API (no tocando la base de datos).
        Assert.Equal(HttpStatusCode.NoContent,
            (await fixture.OwnerClient.PutAsJsonAsync($"/api/v1/users/{member.TenantUserId}/role", new { role = (int)TenantRole.Employee })).StatusCode);

        // El mismo token, que sigue diciendo Manager, ya no puede asignar: es Employee.
        var denied = await member.Client.PostAsJsonAsync($"/api/v1/jobs/{job}/assign", new { tenantUserId = member.TenantUserId });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("auth.forbidden", (await ReadProblemAsync(denied)).Code);
        Assert.Equal(HttpStatusCode.OK, (await member.Client.GetAsync("/api/v1/customers")).StatusCode); // y conserva lo que Employee sí puede
    }

    // ===== Aislamiento entre empresas (caso multi-tenant obligatorio) =====

    [Fact]
    public async Task AnotherCompanysMembersAndInvitations_AreInvisible_AndCannotBeTouched()
    {
        var other = await fixture.RegisterCompanyAsync("Otra empresa");
        var victim = await fixture.SeedMemberAsync(TenantRole.Employee, other.TenantId);
        var victimInvitation = await InviteOkAsync(NewEmail("de-la-otra"), TenantRole.Employee, other.OwnerClient);

        // No las ve al listar...
        Assert.DoesNotContain(await MembersAsync(), m => m.Id == victim.TenantUserId || m.Email == victim.Email);
        Assert.DoesNotContain(await PendingAsync(), i => i.Id == victimInvitation.Id);

        // ...y tocarlas da el MISMO 404 que un Id que no existe en ninguna parte.
        var unknownMember = Guid.NewGuid();
        var change = await fixture.OwnerClient.PutAsJsonAsync($"/api/v1/users/{victim.TenantUserId}/role", new { role = (int)TenantRole.Admin });
        var changeUnknown = await fixture.OwnerClient.PutAsJsonAsync($"/api/v1/users/{unknownMember}/role", new { role = (int)TenantRole.Admin });
        var remove = await fixture.OwnerClient.DeleteAsync($"/api/v1/users/{victim.TenantUserId}");
        var removeUnknown = await fixture.OwnerClient.DeleteAsync($"/api/v1/users/{unknownMember}");
        var revoke = await fixture.OwnerClient.DeleteAsync($"/api/v1/invitations/{victimInvitation.Id}");
        var revokeUnknown = await fixture.OwnerClient.DeleteAsync($"/api/v1/invitations/{Guid.NewGuid()}");

        Assert.All(new[] { change, changeUnknown, remove, removeUnknown, revoke, revokeUnknown }, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Equal(Shape(await ReadProblemAsync(changeUnknown)), Shape(await ReadProblemAsync(change)));
        Assert.Equal(Shape(await ReadProblemAsync(removeUnknown)), Shape(await ReadProblemAsync(remove)));
        Assert.Equal(Shape(await ReadProblemAsync(revokeUnknown)), Shape(await ReadProblemAsync(revoke)));

        // Control positivo: la otra empresa sí lo ve y lo gestiona, y nada de lo de arriba lo cambió.
        Assert.Contains(await MembersAsync(other.OwnerClient), m => m.Id == victim.TenantUserId && m.Role == "Employee");
        Assert.Contains(await PendingAsync(other.OwnerClient), i => i.Id == victimInvitation.Id);
        Assert.Equal(1, await fixture.MembershipCountAsync(victim.TenantUserId));
        Assert.Equal(TenantRole.Employee, await fixture.RoleOfAsync(victim.TenantUserId));
    }

    [Fact]
    public async Task AnAcceptedInvitation_CreatesTheMembershipInTheInvitingCompany_AndInNoOtherOne()
    {
        var other = await fixture.RegisterCompanyAsync("Empresa ajena");
        var email = NewEmail("ficha-propia");
        var created = await InviteOkAsync(email, TenantRole.Technician);

        Assert.Equal(HttpStatusCode.NoContent, (await AcceptAsync(created.Token)).StatusCode);

        Assert.Single(await MembersAsync(), m => m.Email == email);
        Assert.DoesNotContain(await MembersAsync(other.OwnerClient), m => m.Email == email);
    }
}
