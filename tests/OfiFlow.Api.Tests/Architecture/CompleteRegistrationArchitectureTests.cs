using OfiFlow.Api.Tests.Common;

namespace OfiFlow.Api.Tests.Architecture;

/// <summary>
/// Lo que protege el «todo o nada» de completar el registro (spec 021, bloque 3, R10, R13, D1) y que ningún test de comportamiento ve hasta que es tarde. Son escaneos del código (ADR-009):
/// si alguien «simplificara» confirmando por separado, podrían nacer una cuenta sin empresa, una empresa sin dueño o un pendiente gastado sin cuenta, y todos los tests de camino feliz
/// seguirían en verde.
/// </summary>
public class CompleteRegistrationArchitectureTests
{
    private const string Folder = "OfiFlow.Application/Identity/Commands/CompleteRegistration";
    private const string Handler = "src/OfiFlow.Application/Identity/Commands/CompleteRegistration/CompleteRegistrationCommandHandler.cs";

    private static string[] CodeLines(string text) =>
        [.. text.Split('\n').Select(line => line.TrimStart()).Where(line => !line.StartsWith("//", StringComparison.Ordinal))];

    private static string Between(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"No se encontró «{from}».");
        var end = text.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"No se encontró «{to}» después de «{from}».");
        return text[start..end];
    }

    [Fact]
    public void TheCompletionUseCase_ConfirmsExactlyOnce()
    {
        var saves = CodeLines(SourceCode.ReadText(Handler)).Count(line => line.Contains("SaveChangesAsync(", StringComparison.Ordinal));

        Assert.Equal(1, saves);
    }

    [Fact]
    public void ThePiecesTheCompletionUseCaseOrchestrates_NeverConfirmByThemselves()
    {
        // Canjear el pendiente y crear la cuenta de acceso no confirman: lo hace el caso de uso, junto con la empresa y el usuario. Si alguna confirmara sola, un fallo después dejaría un
        // pendiente gastado (o una cuenta de acceso) sin empresa ni dueño.
        var service = SourceCode.ReadText("src/OfiFlow.Infrastructure/Identity/PendingRegistrationService.cs");
        var identity = SourceCode.ReadText("src/OfiFlow.Infrastructure/Identity/IdentityService.cs");

        var pieces = new Dictionary<string, string>
        {
            ["canjear el pendiente"] = Between(service, "public async Task<PendingRegistrationConsumption> ConsumeAsync", "private static PendingRegistrationConsumption Rejected"),
            ["crear la cuenta de acceso"] = Between(identity, "public async Task<IdentityCreationResult> CreateUserAsync", "public Task<bool> IsEmailRegisteredAsync")
        };

        foreach (var (name, body) in pieces)
        {
            var code = string.Join('\n', CodeLines(body));

            // Ni confirman, ni escriben al instante (ExecuteUpdate/ExecuteDelete se saltan el SaveChanges del caso de uso y rompen el todo o nada).
            Assert.DoesNotContain("SaveChanges", code, StringComparison.Ordinal);
            Assert.DoesNotContain("ExecuteUpdate", code, StringComparison.Ordinal);
            Assert.DoesNotContain("ExecuteDelete", code, StringComparison.Ordinal);
            Assert.True(code.Length > 0, name);
        }
    }

    [Fact]
    public void TheCompletionUseCase_CreatesTheTenantTheUserAndTheOwner_InTheSameOperationAsTheConsumption()
    {
        // Las cuatro cosas que cambian están en el mismo fichero y antes de la única confirmación: canjear, cuenta de acceso, empresa/usuario/pertenencia.
        var code = string.Join('\n', CodeLines(SourceCode.ReadText(Handler)));
        var beforeTheSave = code[..code.IndexOf("SaveChangesAsync(", StringComparison.Ordinal)];

        Assert.Contains("pendingRegistrations.ConsumeAsync(", beforeTheSave);
        Assert.Contains("identityService.CreateUserAsync(", beforeTheSave);
        Assert.Contains("Tenant.Create(", beforeTheSave);
        Assert.Contains("TenantUser.CreateOwner(", beforeTheSave);
        Assert.Contains("AcceptTerms(", beforeTheSave);
    }

    [Fact]
    public void TheCompletionUseCase_ChecksTheTokenBeforeHashingThePassword()
    {
        // Un token que no sirve se rechaza sin pagar el hash lento de la contraseña: este endpoint es anónimo y el hash es lo caro.
        var code = string.Join('\n', CodeLines(SourceCode.ReadText(Handler)));

        Assert.True(code.IndexOf("ConsumeAsync(", StringComparison.Ordinal) < code.IndexOf("CreateUserAsync(", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCompletionUseCase_NeverSendsEmail_AndNeverIssuesATokenForASession()
    {
        // Completar no abre sesión (R12, D6): un enlace de correo no es una llave de entrada. Y no envía nada: el aviso de «cuenta creada» no existe (no hay ninguna cuenta que avisar antes).
        var code = string.Join('\n', SourceCode.Files(Folder).SelectMany(file => CodeLines(file.Content)));

        Assert.DoesNotContain("IEmailSender", code);
        Assert.DoesNotContain("IEmailQueue", code);
        Assert.DoesNotContain("ITokenService", code);
        Assert.DoesNotContain("IssueTokens", code);
    }

    [Fact]
    public void OnlyTheCompletionUseCase_CreatesATenant()
    {
        // D1: la cuenta nace al abrir el enlace. Cualquier otro sitio que creara una empresa sería otra puerta para saltarse la confirmación del buzón (el registro inmediato se retiró en el bloque 4).
        var creators = SourceCode.FilesContaining("Tenant.Create(", "OfiFlow.Application")
            .Concat(SourceCode.FilesContaining("Tenant.Create(", "OfiFlow.Infrastructure"))
            .Concat(SourceCode.FilesContaining("Tenant.Create(", "OfiFlow.Api"))
            .Concat(SourceCode.FilesContaining("Tenant.Create(", "OfiFlow.Admin"))
            .Order()
            .ToList();

        Assert.Equal(["src/OfiFlow.Application/Identity/Commands/CompleteRegistration/CompleteRegistrationCommandHandler.cs"], creators);
    }

    [Fact]
    public void TheImmediateRegistration_IsGone_FromTheEndpointsTheLimitsAndTheApplication()
    {
        // D11: dos formas de crear una cuenta serían dos formas de saltarse la confirmación del buzón.
        var endpoints = string.Join('\n', CodeLines(SourceCode.ReadText("src/OfiFlow.Api/Endpoints/AuthEndpoints.cs")));
        var limits = string.Join('\n', CodeLines(SourceCode.ReadText("src/OfiFlow.Api/Common/RateLimiting.cs")));

        Assert.DoesNotContain("MapPost(\"/register\"", endpoints);
        Assert.DoesNotContain("RegisterCommand", endpoints);
        Assert.DoesNotContain("\"auth-register\"", limits);
        Assert.Empty(SourceCode.FilesContaining("RegisterCommand", "OfiFlow.Application"));
    }

    [Fact]
    public void TheCompletionEndpoint_IsRateLimited_NoStore_AndNeverReturnsATokenOrABody()
    {
        var endpoints = SourceCode.ReadText("src/OfiFlow.Api/Endpoints/AuthEndpoints.cs");
        var complete = Between(endpoints, "\"/registration/complete\"", "RequireRateLimiting(RateLimiting.RegistrationComplete)");

        var code = string.Join('\n', CodeLines(complete));
        Assert.Contains("Results.NoContent()", code);
        Assert.Contains("CacheControl = \"no-store\"", code);
        Assert.DoesNotContain("IssueTokens", code);
        Assert.DoesNotContain("Results.Ok(", code);
    }

    [Fact]
    public void TheCompletionEndpoint_HasItsOwnRateLimitBucket_NotTheOneOfRequestingTheRegistration()
    {
        // Cubo propio (R15): completar paga un hash PBKDF2 y no debe gastar el cupo de pedir el registro, ni al revés.
        var limits = SourceCode.ReadText("src/OfiFlow.Api/Common/RateLimiting.cs");

        Assert.Contains("RegistrationComplete = \"auth-registration-complete\"", limits);
        Assert.Contains("options.AddPolicy(RegistrationComplete, context => PerIp(context, permitLimit: 10, TimeSpan.FromHours(1)))", limits);
    }
}
