using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Mail;
using OfiFlow.Infrastructure.Identity;
using OfiFlow.Infrastructure.Persistence;

namespace OfiFlow.Api.Tests.Common;

/// <summary>
/// Crea una empresa y su primer usuario <b>como lo hace una persona</b> (spec 021, D11): pide el registro, lee el enlace del correo que «llega» y lo completa eligiendo la contraseña. Es la
/// única forma de crear una cuenta (el registro inmediato <c>POST /auth/register</c> se retiró), así que todos los tests que necesitan una empresa pasan por aquí y, de paso, ejercitan el
/// flujo real en cada ejecución: si se rompiera, fallarían todos, no solo los suyos.
/// </summary>
/// <remarks>
/// Usa una segunda API sobre <b>la misma base de datos</b> (<see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/>) con el correo «activado» y un transporte que guarda lo enviado; la
/// cola y el servicio que la vacía son los de verdad. La API de cada fixture se queda como estaba (correo apagado), porque otros tests dependen de ello (por ejemplo, que invitar devuelva el
/// token cuando el correo está apagado). Cada petición declara su propia IP: los límites por IP no cortan la preparación (el real se prueba aparte).
/// </remarks>
public sealed class AccountRegistrar : IDisposable
{
    private const string LinkBase = "https://app.ofiflow.example";
    private const string Marker = "#token=";

    private readonly SqlServerApiFactory _factory;
    private readonly RecordingEmailSender _mailbox = new();
    private readonly WebApplicationFactory<Program> _api;
    private readonly HttpClient _client;

    public AccountRegistrar(SqlServerApiFactory factory)
    {
        _factory = factory;
        _api = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Email:LinkBaseUrl", LinkBase);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(_mailbox);
            });
        });
        _client = _api.CreateClient();
    }

    /// <summary>Lo que llegó a ese buzón, para los tests que comprueban el correo (por ejemplo, el aviso de «ya tienes una cuenta»).</summary>
    public IReadOnlyList<EmailMessage> SentTo(string email) => [.. _mailbox.Sent.Where(message => message.To.Value == email)];

    /// <summary>Pide el registro (la primera mitad del flujo) sin completarlo.</summary>
    public Task<HttpResponseMessage> RequestAsync(string companyName, string userName, string email) =>
        PostAsync("/api/v1/auth/registration", new { companyName, userName, email, acceptedTermsVersion = SqlServerApiFactory.TermsVersion });

    /// <summary>
    /// El flujo completo: pide, lee el enlace del correo y completa con la contraseña. Si cualquier paso falla lo dice con el paso y la respuesta, porque una preparación rota no debe
    /// confundirse con un fallo de lo que se está probando. <b>No inicia sesión</b>: el registro viejo tampoco la abría, y una sesión de más cambiaría lo que cuentan los tests de sesiones. El Id
    /// de la empresa se lee de la base de datos.
    /// </summary>
    /// <returns>El Id de la empresa nueva.</returns>
    public async Task<Guid> RegisterAsync(string companyName, string userName, string email, string password)
    {
        var requested = await RequestAsync(companyName, userName, email);
        await EnsureAsync(requested, HttpStatusCode.Accepted, "pedir el registro");

        var token = TokenOf(await WaitForLinkAsync(email));

        var completed = await PostAsync("/api/v1/auth/registration/complete", new { token, newPassword = password });
        await EnsureAsync(completed, HttpStatusCode.NoContent, "completar el registro");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = await db.Set<ApplicationUser>().Where(user => user.NormalizedEmail == email.ToUpperInvariant()).Select(user => user.Id).SingleAsync();

        // Sin filtro de empresa a propósito: aquí no hay ninguna activa, y se busca por el Id de la cuenta recién creada.
        return await db.TenantUsers.IgnoreQueryFilters().Where(membership => membership.UserId == userId).Select(membership => membership.TenantId).SingleAsync();
    }

    private Task<HttpResponseMessage> PostAsync(string route, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, TestClientIpStartupFilter.NewIp());
        return _client.SendAsync(request);
    }

    /// <summary>El primer correo que llega a esa dirección, o nulo si no llega en el tiempo dado (para comprobar el aviso de «ya tienes una cuenta», que no lleva enlace).</summary>
    public async Task<EmailMessage?> FirstMailToAsync(string email, int seconds = 10)
    {
        var limit = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < limit)
        {
            var found = _mailbox.Sent.FirstOrDefault(message => message.To.Value == email);
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(25);
        }

        return null;
    }

    private async Task<EmailMessage> WaitForLinkAsync(string email)
    {
        // El envío es asíncrono (la cola): se espera a que llegue, con un tope.
        var limit = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < limit)
        {
            var found = _mailbox.Sent.FirstOrDefault(message => message.To.Value == email && message.TextBody.Contains(Marker, StringComparison.Ordinal));
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException("La preparación del test falló: el correo con el enlace de completar el registro no llegó en 20 s.");
    }

    private static string TokenOf(EmailMessage message)
    {
        var link = message.TextBody.Split("\r\n").Single(line => line.StartsWith("https://", StringComparison.Ordinal));
        return link[(link.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length)..];
    }

    private static async Task EnsureAsync(HttpResponseMessage response, HttpStatusCode expected, string step)
    {
        if (response.StatusCode != expected)
        {
            throw new InvalidOperationException($"La preparación del test falló al {step}: {(int)response.StatusCode} (se esperaba {(int)expected}). {await response.Content.ReadAsStringAsync()}");
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _api.Dispose();
    }
}
