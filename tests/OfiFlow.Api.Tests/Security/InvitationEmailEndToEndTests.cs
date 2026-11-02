using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Mail;
using OfiFlow.Application.Tenancy;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// Invitar con el correo configurado, por HTTP, con la API real y SQL Server real (spec 017, bloque 2). El transporte es uno que guarda lo enviado: lo que se prueba es el camino
/// completo alrededor de él — la respuesta, el enlace, lo que queda en la base de datos y lo que NO queda en los logs.
/// </summary>
public class InvitationEmailEndToEndTests : IClassFixture<MembersFixture>, IAsyncLifetime
{
    private const string LinkBase = "https://app.ofiflow.example";
    private const string AcceptPrefix = LinkBase + "/auth/accept-invitation#token=";

    private readonly MembersFixture _fixture;
    private readonly RecordingEmailSender _email = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _owner = null!;

    public InvitationEmailEndToEndTests(MembersFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        // La misma API y la misma base de datos que el resto, con el correo «activado»: un transporte que guarda lo enviado y el enlace base del despliegue.
        _factory = _fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Email:LinkBaseUrl", LinkBase);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(_email);
            });
        });

        _owner = _factory.CreateClient();
        _owner.DefaultRequestHeaders.Authorization = _fixture.OwnerClient.DefaultRequestHeaders.Authorization;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _owner.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static string NewEmail() => $"invitada-{Guid.NewGuid():N}@example.com";

    private static string LinkOf(EmailMessage message) =>
        message.TextBody.Split("\r\n").Single(line => line.StartsWith("https://", StringComparison.Ordinal));

    private static string TokenOf(EmailMessage message) => LinkOf(message)[AcceptPrefix.Length..];

    private Task<HttpResponseMessage> InviteAsync(string email, HttpClient? client = null, Action<HttpRequestMessage>? tweak = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/invitations") { Content = JsonContent.Create(new { email, role = (int)TenantRole.Technician }) };
        tweak?.Invoke(request);
        return (client ?? _owner).SendAsync(request);
    }

    // ===== Camino feliz =====

    [Fact]
    public async Task Inviting_SendsTheLink_AndTheResponseCarriesNoToken()
    {
        var email = NewEmail();

        var response = await InviteAsync(email);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.True(body.GetProperty("emailSent").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("token").ValueKind);

        var message = Assert.Single(_email.Sent, m => m.To.Value == email);
        Assert.StartsWith(AcceptPrefix, LinkOf(message));
        // Ni una pista del token en lo que el invitante recibe.
        Assert.DoesNotContain(TokenOf(message), json);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task TheLinkInTheEmail_ReallyAcceptsTheInvitation()
    {
        var email = NewEmail();
        await InviteAsync(email);

        var token = TokenOf(Assert.Single(_email.Sent, m => m.To.Value == email));
        var accept = await _fixture.AcceptInvitationAsync(token, "Persona invitada", MembersFixture.NewPassword());

        Assert.Equal(HttpStatusCode.NoContent, accept.StatusCode);
    }

    [Fact]
    public async Task TheInviterCannotAcceptWhatTheyDoNotKnow_TheTokenIsNowhereInTheirReach()
    {
        // Lo que cierra el riesgo aceptado de ADR-013: ni en la respuesta de invitar ni en la lista de pendientes aparece nada que sirva para aceptar.
        var email = NewEmail();
        var created = await (await InviteAsync(email)).Content.ReadFromJsonAsync<InvitationCreatedDto>();
        var token = TokenOf(Assert.Single(_email.Sent, m => m.To.Value == email));

        var pending = await _owner.GetStringAsync("/api/v1/invitations");

        Assert.NotNull(created);
        Assert.Contains(created.Id.ToString(), pending);
        Assert.DoesNotContain(token, pending);
        Assert.DoesNotContain("token", pending, StringComparison.OrdinalIgnoreCase);
    }

    // ===== R3: el enlace sale de la configuración, nunca de la petición =====

    [Fact]
    public async Task ARequestWithAnotherHostAndOrigin_DoesNotChangeWhereTheLinkPoints()
    {
        var email = NewEmail();

        var response = await InviteAsync(email, tweak: request =>
        {
            request.Headers.Host = "evil.example";
            request.Headers.Add("Origin", "https://evil.example");
            request.Headers.Add("Referer", "https://evil.example/phishing");
            request.Headers.Add("X-Forwarded-Host", "evil.example");
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var message = Assert.Single(_email.Sent, m => m.To.Value == email);
        Assert.StartsWith(AcceptPrefix, LinkOf(message));
        Assert.DoesNotContain("evil.example", message.TextBody);
    }

    // ===== R11: si el correo no sale, no se crea nada =====

    [Theory]
    [InlineData(EmailFailureCategory.Timeout)]
    [InlineData(EmailFailureCategory.Authentication)]
    [InlineData(EmailFailureCategory.Unavailable)]
    public async Task WhenTheEmailCannotBeSent_ItIsABadGateway_NothingIsCreated_AndTheMotiveIsNotRevealed(EmailFailureCategory category)
    {
        var email = NewEmail();
        _email.FailWith = category;
        try
        {
            var response = await InviteAsync(email);

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("invitation.email_not_sent", problem.GetProperty("code").GetString());
            var text = problem.GetRawText();
            Assert.DoesNotContain(category.ToString(), text);
            Assert.DoesNotContain(email, text);

            var pending = await _owner.GetStringAsync("/api/v1/invitations");
            Assert.DoesNotContain(email, pending);
        }
        finally
        {
            _email.FailWith = null;
        }
    }

    [Fact]
    public async Task AfterAFailure_TheSameEmailCanBeInvitedAgain()
    {
        var email = NewEmail();
        _email.FailWith = EmailFailureCategory.Timeout;
        try
        {
            Assert.Equal(HttpStatusCode.BadGateway, (await InviteAsync(email)).StatusCode);
        }
        finally
        {
            _email.FailWith = null;
        }

        // Como no se creó nada, no choca con «ya hay una invitación pendiente».
        Assert.Equal(HttpStatusCode.Created, (await InviteAsync(email)).StatusCode);
    }

    // ===== R15: lo que queda en los logs =====

    [Fact]
    public async Task TheLogs_HaveTheEmailEvents_WithoutTheAddressTheTokenOrTheLink()
    {
        var sentTo = NewEmail();
        var failedTo = NewEmail();

        await InviteAsync(sentTo);
        _email.FailWith = EmailFailureCategory.Authentication;
        try
        {
            await InviteAsync(failedTo);
        }
        finally
        {
            _email.FailWith = null;
        }

        var token = TokenOf(Assert.Single(_email.Sent, m => m.To.Value == sentTo));
        var logs = _fixture.Factory.Logs.Entries;

        Assert.Contains(logs, e => e.EventId == 1407); // control positivo: los eventos existen
        // Los logs son los de toda la clase (otros tests también provocan fallos): basta con que exista alguno, y todos son Warning.
        var failures = logs.Where(e => e.EventId == 1408 && e.Message.Contains("Authentication")).ToList();
        Assert.NotEmpty(failures);
        Assert.All(failures, failure => Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, failure.Level));

        var everything = string.Join('\n', logs.Select(e => e.Message));
        Assert.DoesNotContain(sentTo, everything);
        Assert.DoesNotContain(failedTo, everything);
        Assert.DoesNotContain(token, everything);
        Assert.DoesNotContain(LinkBase, everything);
    }

    // ===== R13: las reglas se comprueban antes de enviar =====

    [Fact]
    public async Task ARefusedInvitation_SendsNoEmail()
    {
        // Un Admin no puede invitar a un Admin (jerarquía): se rechaza y no sale ningún correo.
        var admin = await _fixture.SeedMemberAsync(TenantRole.Admin);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = admin.Client.DefaultRequestHeaders.Authorization;
        var email = NewEmail();

        var response = await client.PostAsJsonAsync("/api/v1/invitations", new { email, role = (int)TenantRole.Admin });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(_email.Sent, m => m.To.Value == email);
    }

    // ===== Aislamiento entre empresas =====

    [Fact]
    public async Task TheEmailNamesTheInvitersCompany()
    {
        var email = NewEmail();
        await InviteAsync(email);

        var body = Assert.Single(_email.Sent, m => m.To.Value == email).TextBody;
        Assert.Contains("«Empresa de miembros»", body);
    }
}
