using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Identity.Commands.ChangePassword;

namespace OfiFlow.Api.Tests.Architecture;

/// <summary>
/// Lo que protege el «todo o nada» y el orden de cambiar la contraseña estando dentro (spec 019, bloque 2, R3, R5, R6) y que ningún test de comportamiento ve hasta que es tarde. Son escaneos del código
/// (ADR-009): si alguien «simplificara» confirmando por separado, o moviera el aviso antes de confirmar, o registrara la contraseña, los tests de camino feliz seguirían en verde.
/// </summary>
public class ChangePasswordArchitectureTests
{
    private const string Handler = "src/OfiFlow.Application/Identity/Commands/ChangePassword/ChangePasswordCommandHandler.cs";
    private const string Command = "src/OfiFlow.Application/Identity/Commands/ChangePassword/ChangePasswordCommand.cs";
    private const string Endpoints = "src/OfiFlow.Api/Endpoints/AccountEndpoints.cs";

    private static string[] CodeLines(string text) =>
        [.. text.Split('\n').Select(line => line.TrimStart()).Where(line => !line.StartsWith("//", StringComparison.Ordinal))];

    private static string Code(string path) => string.Join('\n', CodeLines(SourceCode.ReadText(path)));

    private static string Between(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"No se encontró «{from}».");
        var end = text.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"No se encontró «{to}» después de «{from}».");
        return text[start..end];
    }

    // ===== El todo o nada =====

    [Fact]
    public void TheChangeUseCase_ConfirmsExactlyOnce()
    {
        var saves = CodeLines(SourceCode.ReadText(Handler)).Count(line => line.Contains("SaveChangesAsync(", StringComparison.Ordinal));

        Assert.Equal(1, saves);
    }

    [Fact]
    public void TheSessionsAreClosedWithTheVariantThatKeepsTheCurrentOneAndDoesNotConfirm()
    {
        var code = Code(Handler);

        Assert.Contains("RevokeOtherRefreshTokensWithoutSavingAsync(", code);
        Assert.Contains("currentUser.SessionId", code); // se le pasa la sesión actual: es la que se conserva
        // Las demás variantes: la que confirma por su cuenta (spec 014), la que cierra TODAS sin confirmar (spec 018: cerraría también la actual) y la de cerrar una concreta.
        Assert.DoesNotContain("RevokeAllRefreshTokensAsync(", code);
        Assert.DoesNotContain("RevokeAllRefreshTokensWithoutSavingAsync(", code);
        Assert.DoesNotContain("CloseSessionAsync(", code);
        Assert.DoesNotContain("DeleteRefreshTokensAsync(", code);
    }

    [Fact]
    public void ThePendingRecoveryLinksAreVoided_WithTheVariantThatDoesNotConfirm()
    {
        Assert.Contains("InvalidatePendingWithoutSavingAsync(", Code(Handler));
    }

    [Fact]
    public void TheThreePiecesTheChangeUseCaseOrchestrates_NeverConfirmByThemselves()
    {
        var service = SourceCode.ReadText("src/OfiFlow.Infrastructure/Identity/PasswordResetService.cs");
        var identity = SourceCode.ReadText("src/OfiFlow.Infrastructure/Identity/IdentityService.cs");
        var tokens = SourceCode.ReadText("src/OfiFlow.Infrastructure/Identity/TokenService.cs");

        var pieces = new Dictionary<string, string>
        {
            ["anular los enlaces pendientes"] = Between(service, "public async Task InvalidatePendingWithoutSavingAsync", "private static PasswordResetConsumption Rejected"),
            ["cambiar la contraseña"] = Between(identity, "public async Task<string?> SetPasswordAsync", "public async Task<Guid?> ValidateCredentialsWithoutLockoutAsync"),
            ["cerrar las demás sesiones"] = Between(tokens, "public Task<int> RevokeOtherRefreshTokensWithoutSavingAsync", "public async Task<IReadOnlyList<ActiveSession>> ListActiveSessionsAsync")
        };

        foreach (var (name, body) in pieces)
        {
            var code = string.Join('\n', CodeLines(body));
            Assert.False(code.Contains("SaveChangesAsync(", StringComparison.Ordinal), $"«{name}» no puede confirmar por su cuenta: lo confirma el caso de uso, una sola vez.");
            Assert.False(code.Contains("ExecuteUpdate", StringComparison.Ordinal) || code.Contains("ExecuteDelete", StringComparison.Ordinal), $"«{name}» escribiría al instante, saltándose el todo o nada.");
        }
    }

    // ===== El orden es la seguridad =====

    [Fact]
    public void TheChecksRunInTheSpecOrder_TheCurrentPassword_TheCode_TheDifferentOne_AndOnlyThenTheChange()
    {
        var code = Code(Handler);
        var steps = new[]
        {
            "ConfirmPasswordAsync(",
            "GetStatusAsync(",
            "VerifySecondFactorAsync(",
            "string.Equals(request.NewPassword, request.CurrentPassword",
            "SetPasswordAsync(",
            "RevokeOtherRefreshTokensWithoutSavingAsync(",
            "InvalidatePendingWithoutSavingAsync(",
            "SaveChangesAsync(",
            "TryEnqueue("
        };

        var positions = steps.Select(step => code.IndexOf(step, StringComparison.Ordinal)).ToArray();

        Assert.All(positions.Zip(steps), pair => Assert.True(pair.First >= 0, $"No se encuentra «{pair.Second}» en el manejador."));
        Assert.Equal(positions.OrderBy(p => p), positions); // exactamente en ese orden
    }

    [Fact]
    public void TheNotice_IsQueuedNeverSentInsideTheRequest_AndOnlyAfterConfirming()
    {
        var code = Code(Handler);

        Assert.DoesNotContain(".SendAsync(", code);
        Assert.True(code.IndexOf("TryEnqueue(", StringComparison.Ordinal) > code.IndexOf("SaveChangesAsync(", StringComparison.Ordinal),
            "El aviso va DESPUÉS de confirmar: un aviso de un cambio que no ocurrió no puede existir.");
    }

    // ===== Ningún secreto sale de aquí =====

    [Fact]
    public void NoLogCall_ReceivesAnythingFromTheRequest()
    {
        var lines = CodeLines(SourceCode.ReadText(Handler));

        // Las únicas llamadas de registro llevan el Id de la cuenta y un recuento. Ninguna línea que registre toca «request».
        var logging = lines.Where(line => line.Contains("Log", StringComparison.Ordinal) && line.Contains('(')).ToList();
        Assert.NotEmpty(logging); // control positivo: el evento existe
        Assert.All(logging, line => Assert.DoesNotContain("request", line, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, line => line.Contains("LogChanged(ILogger logger, Guid userId, int closedSessions)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCommand_NeverPrintsItsSecrets_AndHasOnlyTheThreeExpectedFields()
    {
        Assert.Contains("ToString() => nameof(ChangePasswordCommand)", Code(Command));

        // Sin «userId» ni «tenantId»: la cuenta sale del token y nada de la petición puede apuntar a otra (mass assignment, ADR-009 R2).
        var properties = typeof(ChangePasswordCommand).GetProperties().Where(p => p.DeclaringType == typeof(ChangePasswordCommand) && p.Name != "EqualityContract").Select(p => p.Name).Order().ToArray();
        Assert.Equal(["Code", "CurrentPassword", "NewPassword"], properties);
    }

    // ===== Quién puede y por dónde =====

    [Fact]
    public void TheOperation_IsNotInTheClosedListOfTheMandatorySecondFactor()
    {
        // La lista de las operaciones permitidas sin el factor sigue siendo exactamente cuatro (spec 022); esta no está: primero se activa el factor.
        Assert.DoesNotContain("AllowedWithoutSecondFactor", Code(Command));
        Assert.Contains("[RequiresPermission(Permission.AccountChangePassword)]", Code(Command));
    }

    [Fact]
    public void TheEndpoint_IsAuthenticated_HasItsOwnRateLimit_RespondsNoContentAndNeverCaches()
    {
        var endpoints = SourceCode.ReadText(Endpoints);
        var endpoint = Between(endpoints, "group.MapPut(\"/password\"", "group.MapDelete(\"/sessions\"");

        Assert.Contains("RequireRateLimiting(RateLimiting.AccountPassword)", endpoint);
        Assert.Contains("Results.NoContent()", endpoint);
        Assert.Contains("CacheControl = \"no-store\"", endpoint);
        Assert.DoesNotContain("Results.Ok(", endpoint);          // no devuelve nada: la sesión actual sigue valiendo y no hay tokens que entregar
        Assert.DoesNotContain("AllowAnonymous", endpoint);
        // El grupo entero es autenticado.
        Assert.Contains("MapGroup(Route).WithTags(\"Account\").RequireAuthorization()", endpoints);
    }

    [Fact]
    public void TheRateLimitPolicy_IsRegistered_WithItsOwnBucketOfTenPerHour()
    {
        var policies = Code("src/OfiFlow.Api/Common/RateLimiting.cs");

        Assert.Contains("public const string AccountPassword = \"account-password\";", policies);
        Assert.Contains("options.AddPolicy(AccountPassword, context => PerIp(context, permitLimit: 10, TimeSpan.FromHours(1)));", policies);
    }
}
