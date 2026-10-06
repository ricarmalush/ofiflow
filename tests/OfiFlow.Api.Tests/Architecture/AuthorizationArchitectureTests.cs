using MediatR;
using OfiFlow.Application;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Api.Tests.Architecture;

/// <summary>
/// Guardarraíl fail-closed de la spec 008 (ADR-012 R3): toda operación declara su autorización. Aplica
/// las reglas a las operaciones reales y, con operaciones inventadas mal declaradas, comprueba que las
/// reglas muerden: un guardarraíl que nunca ha fallado no demuestra que funcione.
/// </summary>
public class AuthorizationArchitectureTests
{
    // --- Las operaciones reales ---

    [Fact]
    public void EveryOperationOfTheApplication_DeclaresItsAuthorization()
    {
        var operations = typeof(DependencyInjection).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.GetInterfaces().Any(IsMediatRRequest))
            .ToList();

        var violations = AuthorizationDeclarationRules.Violations(operations);

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));

        // Salvaguarda contra un fallo silencioso: si el escaneo no encontrara operaciones, el test pasaría sin comprobar nada.
        Assert.True(operations.Count >= 16, $"Se esperaban al menos las 16 operaciones actuales y se encontraron {operations.Count}.");
    }

    // --- Operaciones inventadas, declaradas bien y mal ---

    [RequiresPermission(Permission.JobsRead)]
    private sealed record Correct : IRequest;

    [RequiresPermission(Permission.JobsRead)]
    [RequiresPermission(Permission.CustomersRead)]
    private sealed record CorrectWithTwoPermissions : IRequest;

    [AllowAnonymousRequest]
    private sealed record CorrectAnonymous : IRequest;

    private sealed record Undeclared : IRequest;

    [AllowAnonymousRequest]
    [RequiresPermission(Permission.JobsRead)]
    private sealed record Contradictory : IRequest;

    [RequiresPermission((Permission)999)]
    private sealed record NonexistentPermission : IRequest;

    [Fact]
    public void Rules_AcceptCorrectDeclarations()
    {
        var violations = AuthorizationDeclarationRules.Violations([typeof(Correct), typeof(CorrectWithTwoPermissions), typeof(CorrectAnonymous)]);

        Assert.Empty(violations);
    }

    [Fact]
    public void Rules_ReportAnOperationWithoutDeclaration_ByName_AndSayWhatToDo()
    {
        var violations = AuthorizationDeclarationRules.Violations([typeof(Undeclared)]);

        var violation = Assert.Single(violations);
        Assert.StartsWith(nameof(Undeclared), violation);
        Assert.Contains("[RequiresPermission", violation);
    }

    [Fact]
    public void Rules_ReportAContradictoryDeclaration()
    {
        var violations = AuthorizationDeclarationRules.Violations([typeof(Contradictory)]);

        var violation = Assert.Single(violations);
        Assert.StartsWith(nameof(Contradictory), violation);
        Assert.Contains("contradictorio", violation);
    }

    [Fact]
    public void Rules_ReportAPermissionThatDoesNotExist()
    {
        var violations = AuthorizationDeclarationRules.Violations([typeof(NonexistentPermission)]);

        var violation = Assert.Single(violations);
        Assert.StartsWith(nameof(NonexistentPermission), violation);
        Assert.Contains("ningún rol", violation);
    }

    [Fact]
    public void Rules_ReportEveryProblem_NotJustTheFirstOne()
    {
        var violations = AuthorizationDeclarationRules.Violations(
            [typeof(Correct), typeof(Undeclared), typeof(Contradictory), typeof(NonexistentPermission)]);

        Assert.Equal(3, violations.Count);
        Assert.DoesNotContain(violations, v => v.StartsWith(nameof(Correct) + ":"));
    }

    private static bool IsMediatRRequest(Type type) =>
        type == typeof(IRequest) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IRequest<>));
}
