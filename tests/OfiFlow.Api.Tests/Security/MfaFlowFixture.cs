using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Identity.Mfa;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Api.Tests.Security;

/// <summary>Un reloj que el test controla: el TOTP cambia cada 30 s y cada paso solo vale una vez, así que se avanza a propósito.</summary>
public sealed class ControlledClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan time) => _now += time;
}

/// <summary>
/// La API entera con SQL Server real y un <b>reloj controlado</b> (spec 010, bloque 7). Con él se puede escribir el ciclo
/// completo del segundo factor con códigos TOTP de verdad: avanzar 30 s para tener un código nuevo, 5 minutos para caducar un
/// desafío o 15 para terminar un bloqueo, sin esperar. El reloj solo gobierna el segundo factor y el bloqueo; los tokens de
/// acceso siguen caducando con el reloj real. Cada petición declara una IP distinta (los límites por IP son otra defensa).
/// </summary>
public sealed class MfaFlowFixture : IAsyncLifetime
{
    public const string RecoveryCodePattern = @"^[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){3}$";

    private readonly SqlServerApiFactory _database = new();

    public ControlledClock Clock { get; } = new(DateTimeOffset.UtcNow);

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public HttpClient Http { get; private set; } = null!;

    public CapturingLoggerProvider Logs => _database.Logs;

    public IServiceProvider Services => Factory.Services;

    public async Task InitializeAsync()
    {
        await _database.StartAsync();

        Factory = _database.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(Clock)));
        Http = Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Http?.Dispose();
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        await _database.DisposeAsync();
    }

    // ===== Peticiones =====

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string? bearer = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(TestClientIpStartupFilter.HeaderName, TestClientIpStartupFilter.NewIp());

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return Http.SendAsync(request);
    }

    public static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    // ===== Cuentas =====

    public sealed class Account(string email, string password)
    {
        public string Email { get; } = email;

        public string Password { get; } = password;

        public string AccessToken { get; set; } = string.Empty;

        public string RefreshToken { get; set; } = string.Empty;

        /// <summary>El secreto TOTP en claro, tal como lo vería la app del móvil.</summary>
        public byte[] Secret { get; set; } = [];

        public List<string> RecoveryCodes { get; } = [];
    }

    /// <summary>Registra una empresa nueva y entra: el Owner de la prueba, sin segundo factor todavía.</summary>
    public async Task<Account> RegisterOwnerAsync(string company = "Empresa de pruebas")
    {
        var account = new Account($"e2e-{Guid.NewGuid():N}@example.com", MembersFixture.NewPassword());

        var register = await SendAsync(HttpMethod.Post, "/api/v1/auth/register",
            new { companyName = company, userName = "Ana Pérez", email = account.Email, password = account.Password });
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);

        await LoginPlainAsync(account);

        return account;
    }

    /// <summary>Login de una cuenta SIN segundo factor: devuelve la sesión y la guarda en la cuenta.</summary>
    public async Task LoginPlainAsync(Account account)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = account.Email, password = account.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var tokens = await JsonOf(response);
        account.AccessToken = tokens.GetProperty("accessToken").GetString()!;
        account.RefreshToken = tokens.GetProperty("refreshToken").GetString()!;
    }

    /// <summary>Invita a una persona con un rol, ella acepta con su contraseña y entra: un miembro con credenciales conocidas.</summary>
    public async Task<Account> InviteAndJoinAsync(Account owner, TenantRole role)
    {
        var member = new Account($"miembro-{Guid.NewGuid():N}@example.com", MembersFixture.NewPassword());

        var invite = await SendAsync(HttpMethod.Post, "/api/v1/invitations", new { email = member.Email, role = (int)role }, owner.AccessToken);
        Assert.Equal(HttpStatusCode.Created, invite.StatusCode);
        var token = (await JsonOf(invite)).GetProperty("token").GetString();

        var accept = await SendAsync(HttpMethod.Post, "/api/v1/auth/accept-invitation", new { token, name = $"Miembro {Guid.NewGuid():N}", password = member.Password });
        Assert.Equal(HttpStatusCode.NoContent, accept.StatusCode);

        await LoginPlainAsync(member);

        return member;
    }

    /// <summary>El Id de la pertenencia de un miembro, tal como lo ve el Owner en el listado.</summary>
    public async Task<Guid> TenantUserIdOfAsync(Account owner, Account member)
    {
        var users = await JsonOf(await SendAsync(HttpMethod.Get, "/api/v1/users", bearer: owner.AccessToken));

        return users.EnumerateArray().Single(user => user.GetProperty("email").GetString() == member.Email).GetProperty("id").GetGuid();
    }

    // ===== Segundo factor =====

    /// <summary>El código TOTP de ahora (más o menos <paramref name="stepOffset"/> pasos), calculado con el secreto de la cuenta.</summary>
    public string CodeNow(Account account, int stepOffset = 0) =>
        Totp.Compute(account.Secret, Totp.StepAt(Clock.GetUtcNow()) + stepOffset);

    /// <summary>Un código que no se ha usado nunca: se avanza un paso de 30 s y se calcula el de ese instante.</summary>
    public string FreshCode(Account account)
    {
        Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));

        return CodeNow(account);
    }

    /// <summary>Pide el secreto con la contraseña y lo guarda en la cuenta (como lo escanearía la app).</summary>
    public async Task<HttpResponseMessage> SetupAsync(Account account)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/account/mfa/setup", new { password = account.Password }, account.AccessToken);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            account.Secret = Base32Decode((await JsonOf(response)).GetProperty("secret").GetString()!);
        }

        return response;
    }

    /// <summary>Pide el secreto, lo confirma con un código válido y deja la cuenta con el MFA activo y sus códigos de recuperación.</summary>
    public async Task EnableMfaAsync(Account account)
    {
        Assert.Equal(HttpStatusCode.OK, (await SetupAsync(account)).StatusCode);

        var enable = await SendAsync(HttpMethod.Post, "/api/v1/account/mfa/enable", new { code = CodeNow(account) }, account.AccessToken);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);

        account.RecoveryCodes.Clear();
        account.RecoveryCodes.AddRange((await JsonOf(enable)).GetProperty("codes").EnumerateArray().Select(code => code.GetString()!));
        Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds * 2)); // el primer código ya está gastado: el siguiente paso trae uno nuevo
    }

    /// <summary>Primer paso del login de una cuenta con MFA: devuelve el desafío (y comprueba que no hay sesión).</summary>
    public async Task<string> LoginForChallengeAsync(Account account)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email = account.Email, password = account.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await JsonOf(response);
        Assert.True(body.GetProperty("mfaRequired").GetBoolean());
        Assert.False(body.TryGetProperty("accessToken", out _));

        return body.GetProperty("challenge").GetString()!;
    }

    public Task<HttpResponseMessage> VerifyAsync(string challenge, string code) =>
        SendAsync(HttpMethod.Post, "/api/v1/auth/mfa/verify", new { challenge, code });

    /// <summary>Segundo paso con un código válido: devuelve la sesión y la guarda en la cuenta.</summary>
    public async Task LoginWithMfaAsync(Account account, string? code = null)
    {
        var challenge = await LoginForChallengeAsync(account);
        var response = await VerifyAsync(challenge, code ?? FreshCode(account));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var tokens = await JsonOf(response);
        account.AccessToken = tokens.GetProperty("accessToken").GetString()!;
        account.RefreshToken = tokens.GetProperty("refreshToken").GetString()!;
    }

    /// <summary>Una cuenta nueva con el MFA ya activo y la sesión abierta con el segundo factor.</summary>
    public async Task<Account> NewMfaAccountAsync()
    {
        var account = await RegisterOwnerAsync();
        await EnableMfaAsync(account);
        await LoginWithMfaAsync(account);

        return account;
    }

    /// <summary>Un estado completo de la respuesta para comparar dos fallos: si cambia algo observable, estas cadenas difieren.</summary>
    public static async Task<string> FingerprintAsync(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(header => header.Key is not ("Date" or "Content-Length"))
            .OrderBy(header => header.Key, StringComparer.Ordinal)
            .Select(header => $"{header.Key}={string.Join(",", header.Value)}");

        return $"{(int)response.StatusCode}|{string.Join(";", headers)}|{await response.Content.ReadAsStringAsync()}";
    }

    // ===== Base32 (RFC 4648): lo que hace la app del móvil con el secreto que se le entrega =====

    public static byte[] Base32Decode(string encoded)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;

        foreach (var character in encoded.TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(character);
            bits += 5;

            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
