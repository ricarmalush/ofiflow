using MediatR;
using OfiFlow.Application;

namespace OfiFlow.Api.Tests.Architecture;

/// <summary>
/// Guardarraíles de ADR-009 (R1 y R2): convierten reglas de seguridad en tests que fallan si
/// alguien las incumple, en vez de depender de acordarse en cada revisión.
/// </summary>
public class SecurityArchitectureTests
{
    /// <summary>
    /// Únicos ficheros autorizados a saltarse el Global Query Filter de tenant (ADR-002).
    /// Añadir uno nuevo exige justificarlo en el código y en ADR-009.
    /// </summary>
    private static readonly string[] IgnoreQueryFiltersAllowList =
    [
        "LoginCommandHandler.cs", // todavía no hay tenant activo: es lo que el login determina
        "TokenService.cs"         // el refresh relee el rol del usuario en su tenant
    ];

    [Theory]
    [InlineData("FromSqlRaw")]
    [InlineData("ExecuteSqlRaw")]
    public void SourceCode_DoesNotUseRawSqlApis(string forbiddenApi)
    {
        var offenders = SourceFilesContaining(forbiddenApi);

        Assert.True(offenders.Count == 0,
            $"'{forbiddenApi}' está prohibido (ADR-009 R1: riesgo de inyección SQL). " +
            $"Usa LINQ, o FromSql/SqlQuery con interpolación (parametrizado). Aparece en: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void IgnoreQueryFilters_IsOnlyUsedInTheAllowList()
    {
        var offenders = SourceFilesContaining("IgnoreQueryFilters(")
            .Where(file => !IgnoreQueryFiltersAllowList.Contains(Path.GetFileName(file)))
            .ToList();

        Assert.True(offenders.Count == 0,
            "IgnoreQueryFilters() se salta el aislamiento de tenant (ADR-002, ADR-009 R2) y solo se permite en " +
            $"{string.Join(", ", IgnoreQueryFiltersAllowList)}. Aparece también en: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void NoCommandOrQuery_AcceptsTenantIdFromTheClient()
    {
        var offenders = typeof(DependencyInjection).Assembly.GetTypes()
            .Where(type => type.GetInterfaces().Any(IsMediatRRequest))
            .Where(type => type.GetProperty("TenantId") is not null)
            .Select(type => type.FullName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Un Command/Query no puede recibir TenantId: se toma siempre del JWT vía ITenantContext " +
            $"(ADR-002, ADR-009 R2, protección contra mass assignment). Lo tienen: {string.Join(", ", offenders)}");
    }

    private static bool IsMediatRRequest(Type type) =>
        type == typeof(IRequest) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IRequest<>));

    private static List<string> SourceFilesContaining(string text) =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsGenerated(file))
            .Where(file => File.ReadAllText(file).Contains(text, StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(RepositoryRoot(), file))
            .ToList();

    // bin/obj contienen código generado; Migrations lo genera EF Core.
    private static bool IsGenerated(string file)
    {
        var separator = Path.DirectorySeparatorChar;
        return file.Contains($"{separator}obj{separator}") ||
               file.Contains($"{separator}bin{separator}") ||
               file.Contains($"{separator}Migrations{separator}");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OfiFlow.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("No se encuentra la raíz del repositorio (OfiFlow.slnx).");
    }
}
