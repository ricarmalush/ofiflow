using OfiFlow.Application.Quotes.Commands.CreateQuote;
using OfiFlow.Application.Quotes.Commands.UpdateQuote;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Api.Tests.Architecture;

/// <summary>
/// Lo que protege el dinero y el aislamiento de los presupuestos (spec 023, bloque 2, R5, R12 y R14) y que ningún test de comportamiento ve hasta que es tarde. Son escaneos del código
/// (ADR-009): si alguien aceptara un total en la petición, calculara un importe en otro sitio, se saltara el filtro por empresa o registrara un título, los tests de camino feliz seguirían en verde.
/// </summary>
public class QuoteArchitectureTests
{
    private const string Application = "OfiFlow.Application/Quotes";
    private const string Endpoints = "src/OfiFlow.Api/Endpoints/QuoteEndpoints.cs";

    private static string[] CodeLines(string text) =>
        [.. text.Split('\n').Select(line => line.TrimStart()).Where(line => !line.StartsWith("//", StringComparison.Ordinal) && !line.StartsWith("///", StringComparison.Ordinal))];

    private static string Code(string text) => string.Join('\n', CodeLines(text));

    private static List<SourceFile> QuoteApplicationFiles() => [.. SourceCode.Files("OfiFlow.Application/Quotes")];

    private static List<SourceFile> QuoteHandlers() => [.. QuoteApplicationFiles().Where(f => f.Path.EndsWith("Handler.cs", StringComparison.Ordinal))];

    // ===== R5: el servidor es la única fuente del dinero =====

    [Fact]
    public void NoRequestCarriesATotalANumberAStatusOrACompany()
    {
        var forbidden = new[] { "Total", "Net", "Vat", "Number", "Status", "Tenant", "Revision", "SentAt", "DecidedAt" };

        // Los comandos de Application que crean o modifican, y el cuerpo del PUT (que está en la API).
        var properties = new[] { typeof(CreateQuoteCommand), typeof(UpdateQuoteCommand) }.SelectMany(t => t.GetProperties().Select(p => $"{t.Name}.{p.Name}")).ToList();
        var request = typeof(OfiFlow.Api.Endpoints.QuoteEndpoints).GetNestedType("UpdateQuoteRequest", System.Reflection.BindingFlags.NonPublic)!;
        properties.AddRange(request.GetProperties().Select(p => $"UpdateQuoteRequest.{p.Name}"));

        var offenders = properties.Where(p => forbidden.Any(word => p[(p.IndexOf('.') + 1)..].Contains(word, StringComparison.Ordinal))).ToList();

        Assert.True(offenders.Count == 0, $"Una petición de presupuesto no puede traer estos datos: los pone el servidor (R5, R6, R12): {string.Join(", ", offenders)}");
        Assert.NotEmpty(properties);
    }

    [Fact]
    public void TheStoredTotals_AreOnlyAssignedByTheQuoteItself()
    {
        var assigners = new[] { "NetCents =", "VatCents =", "TotalCents =" };

        var offenders = SourceCode.Files()
            .Where(file => !file.Path.EndsWith("OfiFlow.Domain/Quotes/Quote.cs", StringComparison.Ordinal))
            .Where(file => CodeLines(file.Content).Any(line => assigners.Any(a => line.Contains(a, StringComparison.Ordinal) && !line.Contains("==", StringComparison.Ordinal) && !line.StartsWith("public", StringComparison.Ordinal))))
            .Select(file => file.Path)
            .ToList();

        // La calculadora devuelve QuoteTotals (con propiedades de solo lectura inicializadas por constructor) y el DTO las lee: ningún otro fichero las asigna.
        Assert.True(offenders.Count == 0, $"Solo Quote.Recalculate fija los totales guardados (R5). Los asignan: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheMoneyIsRoundedInOnePlace_AndNeverInAFloatingPointType()
    {
        var roundingAllowed = new[] { "OfiFlow.Domain/Quotes/QuoteCalculator.cs", "OfiFlow.Domain/Quotes/Quote.cs", "OfiFlow.Application/Quotes/QuoteRules.cs" };

        // Redondear dinero (decimal.Round / Math.Round / MidpointRounding) solo en la calculadora; el dominio y el validador lo usan para comprobar los decimales de la cantidad.
        var rounders = SourceCode.Files()
            .Where(file => CodeLines(file.Content).Any(line => line.Contains("decimal.Round(", StringComparison.Ordinal) || line.Contains("Math.Round(", StringComparison.Ordinal) || line.Contains("MidpointRounding", StringComparison.Ordinal)))
            .Select(file => file.Path)
            .Where(path => path.Contains("Quote", StringComparison.Ordinal) || path.Contains("Quotes", StringComparison.Ordinal))
            .Where(path => !roundingAllowed.Any(allowed => path.EndsWith(allowed, StringComparison.Ordinal)))
            .ToList();
        Assert.True(rounders.Count == 0, $"El redondeo del dinero vive en QuoteCalculator: {string.Join(", ", rounders)}");

        var calculator = Code(SourceCode.ReadText("src/OfiFlow.Domain/Quotes/QuoteCalculator.cs"));
        Assert.Contains("MidpointRounding.AwayFromZero", calculator);
        Assert.DoesNotContain("MidpointRounding.ToEven", calculator);

        // Ningún tipo de coma flotante en el dinero de los presupuestos: un double no representa 0,1 con exactitud.
        foreach (var file in SourceCode.Files("OfiFlow.Domain/Quotes").Concat(QuoteApplicationFiles()))
        {
            var code = Code(file.Content);
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(code, @"\b(double|float)\b"), $"{file.Path} usa coma flotante");
        }
    }

    // ===== R12: el aislamiento =====

    [Fact]
    public void NoQuoteOperation_SkipsTheGlobalTenantFilter()
    {
        var offenders = QuoteApplicationFiles()
            .Concat(SourceCode.Files("OfiFlow.Domain/Quotes"))
            .Concat(SourceCode.Files("OfiFlow.Application/Customers/Commands/DeleteCustomer"))
            .Where(file => file.Content.Contains("IgnoreQueryFilters", StringComparison.Ordinal))
            .Select(file => file.Path)
            .ToList();

        Assert.True(offenders.Count == 0, $"Las operaciones de presupuestos van siempre por el filtro de empresa: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheEndpoints_NeverReadACompanyFromTheRequest_AndAllRequireASession()
    {
        var code = Code(SourceCode.ReadText(Endpoints));

        Assert.DoesNotContain("tenantId", code, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".RequireAuthorization()", code);
        Assert.DoesNotContain("AllowAnonymous", code);

        var program = SourceCode.ReadText("src/OfiFlow.Api/Program.cs");
        Assert.Contains("app.MapQuoteEndpoints();", program);
    }

    [Fact]
    public void TheQuoteEndpoints_AreExactlyTheEightOfTheSpec()
    {
        var code = Code(SourceCode.ReadText(Endpoints));
        var routes = System.Text.RegularExpressions.Regex.Matches(code, @"group\.Map(Get|Post|Put|Delete)\(""([^""]*)""")
            .Select(m => $"{m.Groups[1].Value.ToUpperInvariant()} {m.Groups[2].Value}")
            .Order()
            .ToList();

        Assert.Equal(
            ["DELETE /{id:guid}", "GET /", "GET /{id:guid}", "POST /", "POST /{id:guid}/accept", "POST /{id:guid}/reject", "POST /{id:guid}/send", "PUT /{id:guid}"],
            routes);
    }

    // ===== R14: la puerta del segundo factor =====

    [Fact]
    public void NoQuoteOperation_IsAllowedWithoutTheSecondFactor()
    {
        var offenders = QuoteApplicationFiles().Where(file => file.Content.Contains("AllowedWithoutSecondFactor", StringComparison.Ordinal)).Select(file => file.Path).ToList();

        Assert.True(offenders.Count == 0, $"Ninguna operación de presupuestos está en la lista cerrada del segundo factor (spec 022): {string.Join(", ", offenders)}");
    }

    // ===== La confirmación y los registros =====

    [Fact]
    public void EveryWritingHandler_ConfirmsInExactlyOnePlace_AndTheQueriesNeverConfirm()
    {
        foreach (var handler in QuoteHandlers())
        {
            var saves = CodeLines(handler.Content).Count(line => line.Contains("SaveChangesAsync(", StringComparison.Ordinal));
            var isQuery = handler.Path.Contains("/Queries/", StringComparison.Ordinal);

            Assert.True(saves == (isQuery ? 0 : 1), $"{handler.Path}: {saves} confirmaciones (las consultas ninguna; cada comando, una).");
            if (isQuery)
            {
                Assert.Contains("AsNoTracking()", handler.Content);
            }
        }

        Assert.Equal(8, QuoteHandlers().Count);
    }

    [Fact]
    public void NothingOfAQuote_CanReachTheLogs()
    {
        // Ningún caso de uso de presupuestos escribe en el registro: ni título, ni notas, ni descripciones, ni importes (los datos comerciales no se registran).
        foreach (var file in QuoteApplicationFiles().Concat(SourceCode.Files("OfiFlow.Domain/Quotes")))
        {
            var code = Code(file.Content);
            Assert.False(code.Contains("ILogger", StringComparison.Ordinal) || code.Contains("LoggerMessage", StringComparison.Ordinal) || code.Contains("Console.Write", StringComparison.Ordinal) || code.Contains("Debug.Write", StringComparison.Ordinal) || code.Contains("Trace.Write", StringComparison.Ordinal),
                $"{file.Path} escribe en el registro: un presupuesto lleva datos comerciales que no se registran.");
        }

        // Y tampoco el endpoint, por ningún camino: el registro tipado, la consola o la traza de depuración.
        var endpoints = Code(SourceCode.ReadText(Endpoints));
        foreach (var way in new[] { "ILogger", "LoggerMessage", "Console.Write", "Debug.Write", "Trace.Write", "Log." })
        {
            Assert.DoesNotContain(way, endpoints);
        }
    }

    [Fact]
    public void TheCommandsWithFreeText_NeverPrintTheirContent()
    {
        const string marker = "contenido-comercial-reservado";
        QuoteItemInput[] items = [new(marker, 1, 987654321, 21)];

        var texts = new[]
        {
            new CreateQuoteCommand(Guid.NewGuid(), null, marker, marker, null, items).ToString(),
            new UpdateQuoteCommand(Guid.NewGuid(), "AQID", null, marker, marker, null, items).ToString()
        };

        Assert.All(texts, text =>
        {
            Assert.DoesNotContain(marker, text);
            Assert.DoesNotContain("987654321", text);
        });
    }
}
