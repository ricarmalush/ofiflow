using OfiFlow.Api.Tests.Common;

namespace OfiFlow.Api.Tests.Architecture;

/// <summary>
/// Lo que protege el «todo o nada» de restablecer la contraseña (spec 018, bloque 3, R9, D6) y que ningún test de comportamiento ve hasta que es tarde. Son escaneos del código (ADR-009):
/// si alguien «simplificara» confirmando por separado, la contraseña podría cambiar con las sesiones vivas (o al revés) y todos los tests de camino feliz seguirían en verde.
/// </summary>
public class PasswordResetArchitectureTests
{
    private const string Folder = "OfiFlow.Application/Identity/Commands/ResetPassword";
    private const string Handler = "src/OfiFlow.Application/Identity/Commands/ResetPassword/ResetPasswordCommandHandler.cs";

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
    public void TheResetUseCase_ConfirmsExactlyOnce()
    {
        var saves = CodeLines(SourceCode.ReadText(Handler)).Count(line => line.Contains("SaveChangesAsync(", StringComparison.Ordinal));

        Assert.Equal(1, saves);
    }

    [Fact]
    public void TheResetUseCase_ClosesTheSessionsWithTheVariantThatDoesNotConfirm()
    {
        // La variante que confirma (spec 014) dejaría las sesiones cerradas aunque cambiar la contraseña fallara después.
        var code = string.Join('\n', CodeLines(SourceCode.ReadText(Handler)));

        Assert.Contains("RevokeAllRefreshTokensWithoutSavingAsync(", code);
        Assert.DoesNotContain("RevokeAllRefreshTokensAsync(", code);
    }

    [Fact]
    public void TheThreePiecesTheResetUseCaseOrchestrates_NeverConfirmByThemselves()
    {
        var service = SourceCode.ReadText("src/OfiFlow.Infrastructure/Identity/PasswordResetService.cs");
        var identity = SourceCode.ReadText("src/OfiFlow.Infrastructure/Identity/IdentityService.cs");
        var tokens = SourceCode.ReadText("src/OfiFlow.Infrastructure/Identity/TokenService.cs");

        var pieces = new Dictionary<string, string>
        {
            ["canjear el token"] = Between(service, "public async Task<PasswordResetConsumption> ConsumeAsync", "private static PasswordResetConsumption Rejected"),
            ["cambiar la contraseña"] = Between(identity, "public async Task<string?> SetPasswordAsync", "public async Task<Guid?> ValidateCredentialsWithoutLockoutAsync"),
            ["cerrar las sesiones"] = Between(tokens, "private async Task<int> RevokeAllActiveAsync", "public async Task<IReadOnlyList<ActiveSession>> ListActiveSessionsAsync")
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
    public void TheResetUseCase_NeverTouchesTheSecondFactor()
    {
        // R11, D7: controlar el buzón basta para elegir una contraseña, no para entrar. Ni el caso de uso ni el cambio de contraseña saben que existe el MFA.
        var usecase = SourceCode.Files(Folder).SelectMany(file => CodeLines(file.Content)).Where(line => line.Contains("Mfa", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(usecase);

        var identity = SourceCode.ReadText("src/OfiFlow.Infrastructure/Identity/IdentityService.cs");
        var setPassword = string.Join('\n', CodeLines(Between(identity, "public async Task<string?> SetPasswordAsync", "public async Task<Guid?> ValidateCredentialsWithoutLockoutAsync")));
        Assert.DoesNotContain("Mfa", setPassword, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheResetUseCase_NeverSendsEmailInsideTheRequest_ItOnlyQueuesTheNotice()
    {
        var code = string.Join('\n', SourceCode.Files(Folder).SelectMany(file => CodeLines(file.Content)));

        Assert.DoesNotContain(".SendAsync(", code);
        Assert.DoesNotContain("IEmailSender", code);
        Assert.Contains("emailQueue.TryEnqueue(", code);
    }

    [Fact]
    public void TheResetEndpoint_IsRateLimited_AndNeverLogsInTheCaller()
    {
        var endpoints = SourceCode.ReadText("src/OfiFlow.Api/Endpoints/AuthEndpoints.cs");
        var reset = Between(endpoints, "\"/reset-password\"", "RequireRateLimiting(RateLimiting.ResetPassword)");

        // Restablecer no da sesión (se entra con el login de siempre, y con el segundo factor si lo hay): la respuesta no puede llevar tokens.
        var code = string.Join('\n', CodeLines(reset));
        Assert.DoesNotContain("IssueTokens", code);
        Assert.Contains("Results.NoContent()", code);
    }
}
