using System.Reflection;
using System.Text.RegularExpressions;
using OfiFlow.Application.Company;
using OfiFlow.Application.Company.Commands.UpsertCompanyProfile;
using OfiFlow.Application.Company.Queries.GetCompanyProfile;

namespace OfiFlow.Api.Tests.Architecture;

/// <summary>
/// Lo que protege el aislamiento y los datos personales del perfil fiscal (spec 024, bloque 2, R8, R9 y R10) y que ningún test de comportamiento ve hasta que es tarde. Son escaneos del código
/// (ADR-009): si alguien dejara pasar una empresa en la petición, se saltara el filtro, registrara un NIF o dejara la lectura en una caché, los tests de camino feliz seguirían en verde.
/// </summary>
public class CompanyArchitectureTests
{
    private const string Endpoints = "src/OfiFlow.Api/Endpoints/CompanyEndpoints.cs";

    private static string[] CodeLines(string text) =>
        [.. text.Split('\n').Select(line => line.TrimStart()).Where(line => !line.StartsWith("//", StringComparison.Ordinal) && !line.StartsWith("///", StringComparison.Ordinal))];

    private static string Code(string text) => string.Join('\n', CodeLines(text));

    private static List<SourceFile> ApplicationFiles() => [.. SourceCode.Files("OfiFlow.Application/Company")];

    private static List<SourceFile> DomainFiles() => [.. SourceCode.Files("OfiFlow.Domain/Company")];

    // ===== R8: el aislamiento =====

    [Fact]
    public void NoRequestCarriesACompany()
    {
        var properties = new[] { typeof(UpsertCompanyProfileCommand), typeof(GetCompanyProfileQuery) }
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.DeclaringType == type).Select(p => $"{type.Name}.{p.Name}"))
            .ToList();

        Assert.Contains("UpsertCompanyProfileCommand.TaxId", properties); // control positivo: se están leyendo las propiedades de verdad

        // Ni una empresa, ni un Id (el perfil se elige por la empresa del token, no por clave), ni fechas de auditoría: las pone el servidor.
        Assert.DoesNotContain(properties, p => p.EndsWith(".Id", StringComparison.Ordinal) || p.Contains("Tenant", StringComparison.OrdinalIgnoreCase) || p.Contains("CreatedAt", StringComparison.Ordinal) || p.Contains("UpdatedAt", StringComparison.Ordinal));
    }

    [Fact]
    public void NoCompanyOperation_SkipsTheGlobalTenantFilter()
    {
        var offenders = ApplicationFiles().Concat(DomainFiles())
            .Where(file => file.Content.Contains("IgnoreQueryFilters", StringComparison.Ordinal))
            .Select(file => file.Path)
            .ToList();

        Assert.True(offenders.Count == 0, $"Las operaciones del perfil van siempre por el filtro de empresa: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheEndpoints_NeverReadACompanyFromTheRequest_AndAllRequireASession()
    {
        var code = Code(SourceCode.ReadText(Endpoints));

        Assert.DoesNotContain("tenantId", code, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".RequireAuthorization()", code);
        Assert.DoesNotContain("AllowAnonymous", code);
        Assert.Contains("app.MapCompanyEndpoints();", SourceCode.ReadText("src/OfiFlow.Api/Program.cs"));
    }

    [Fact]
    public void TheCompanyEndpoints_AreExactlyTheTwoOfTheSpec()
    {
        var routes = Regex.Matches(Code(SourceCode.ReadText(Endpoints)), @"group\.Map(Get|Post|Put|Delete)\(""([^""]*)""")
            .Select(m => $"{m.Groups[1].Value.ToUpperInvariant()} {m.Groups[2].Value}")
            .Order()
            .ToList();

        Assert.Equal(["GET /", "PUT /"], routes);
        Assert.Contains("\"/api/v1/company/profile\"", SourceCode.ReadText(Endpoints));
    }

    [Fact]
    public void TheRead_IsNeverCached_BecauseItCarriesPersonalData()
    {
        var code = Code(SourceCode.ReadText(Endpoints));
        var read = code[code.IndexOf("group.MapGet", StringComparison.Ordinal)..code.IndexOf("group.MapPut", StringComparison.Ordinal)];

        Assert.Contains("CacheControl = \"no-store\"", read);
    }

    // ===== R9: la puerta del segundo factor =====

    [Fact]
    public void NoCompanyOperation_IsAllowedWithoutTheSecondFactor()
    {
        var offenders = ApplicationFiles().Where(file => file.Content.Contains("AllowedWithoutSecondFactor", StringComparison.Ordinal)).Select(file => file.Path).ToList();

        Assert.True(offenders.Count == 0, $"Ninguna operación del perfil está en la lista cerrada del segundo factor (spec 022): {string.Join(", ", offenders)}");
    }

    // ===== R10: ningún dato del perfil llega al registro =====

    [Fact]
    public void OnlyTheUpsertHandler_WritesToTheLog_WithExactlyTheThreeAllowedValues()
    {
        // Ningún fichero del perfil escribe en el registro salvo el handler de guardar, y este solo con el evento 1409.
        var logging = new[] { "ILogger", "LoggerMessage", "Console.Write", "Debug.Write", "Trace.Write" };
        var writers = ApplicationFiles().Concat(DomainFiles())
            .Where(file => logging.Any(way => Code(file.Content).Contains(way, StringComparison.Ordinal)))
            .Select(file => file.Path)
            .ToList();

        Assert.Equal(["src/OfiFlow.Application/Company/Commands/UpsertCompanyProfile/UpsertCompanyProfileCommandHandler.cs"], writers);

        var handler = Code(SourceCode.ReadText(writers[0]));
        Assert.Single(Regex.Matches(handler, @"\[LoggerMessage"));
        Assert.Contains("EventId = SecurityEventIds.CompanyProfileChanged", handler);

        // La firma del registro: solo quién, la empresa y si fue un alta. Ni una cadena más, ni el comando.
        var signature = Regex.Match(handler, @"private static partial void LogChanged\(([^)]*)\)");
        Assert.True(signature.Success);
        Assert.Equal(["ILogger logger", "Guid? userId", "Guid tenantId", "bool created"], signature.Groups[1].Value.Split(',').Select(p => p.Trim()).ToArray());

        // Y las llamadas no pasan nada del comando.
        foreach (Match call in Regex.Matches(handler, @"LogChanged\(logger, ([^;]*)\);"))
        {
            Assert.DoesNotContain("request", call.Groups[1].Value, StringComparison.Ordinal);
        }

        // El endpoint tampoco escribe nada.
        var endpoints = Code(SourceCode.ReadText(Endpoints));
        foreach (var way in new[] { "ILogger", "LoggerMessage", "Console.Write", "Debug.Write", "Trace.Write", "Log." })
        {
            Assert.DoesNotContain(way, endpoints);
        }
    }

    [Fact]
    public void TheDomainErrors_NeverCarryTheValue_BecauseATaxIdIsPersonalData()
    {
        // Todo `new DomainException(...)` del perfil lleva solo un código, ningún argumento.
        var throws = DomainFiles().SelectMany(file => Regex.Matches(Code(file.Content), @"new DomainException\(([^)]*)\)").Select(m => m.Groups[1].Value)).ToList();

        Assert.NotEmpty(throws);
        Assert.All(throws, argument => Assert.StartsWith("CompanyErrors.", argument, StringComparison.Ordinal));
        Assert.All(throws, argument => Assert.DoesNotContain(",", argument, StringComparison.Ordinal));
    }

    [Fact]
    public void TheTypesThatHoldPersonalData_NeverPrintTheirContent()
    {
        const string marker = "contenido-personal-reservado";
        var command = new UpsertCompanyProfileCommand(marker, "12345678Z", marker, "28001", marker, marker, "600123456", "reservado@example.com").ToString();
        var dto = new CompanyProfileDto(marker, "12345678Z", marker, "28001", marker, marker, "600123456", "reservado@example.com", IsComplete: true).ToString();

        Assert.All(new[] { command, dto }, text =>
        {
            Assert.DoesNotContain(marker, text);
            Assert.DoesNotContain("12345678Z", text);
            Assert.DoesNotContain("600123456", text);
            Assert.DoesNotContain("reservado@example.com", text);
        });
    }

    // ===== La confirmación y la lectura =====

    [Fact]
    public void TheQuery_NeverConfirms_AndReadsWithoutTracking()
    {
        var query = SourceCode.ReadText("src/OfiFlow.Application/Company/Queries/GetCompanyProfile/GetCompanyProfileQueryHandler.cs");

        Assert.DoesNotContain("SaveChanges", Code(query));
        Assert.Contains("AsNoTracking()", query);
    }

    [Fact]
    public void TheHandlers_AreExactlyTheTwoOfTheSpec()
    {
        Assert.Equal(
            ["GetCompanyProfileQueryHandler.cs", "UpsertCompanyProfileCommandHandler.cs"],
            ApplicationFiles().Where(f => f.Path.EndsWith("Handler.cs", StringComparison.Ordinal)).Select(f => Path.GetFileName(f.Path)).Order().ToArray());
    }
}
