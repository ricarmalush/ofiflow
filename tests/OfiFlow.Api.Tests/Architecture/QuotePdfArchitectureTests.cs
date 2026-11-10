using System.Text.RegularExpressions;
using OfiFlow.Application.Quotes.Pdf;

namespace OfiFlow.Api.Tests.Architecture;

/// <summary>
/// Lo que protege el PDF de un presupuesto (spec 025, bloque 2) y que ningún test de camino feliz ve hasta que es tarde: la librería no sale de Infrastructure, el dinero no pasa por coma flotante,
/// el nombre del fichero no lleva texto de nadie, la descarga tiene su límite y su cabecera <c>no-store</c>, y ninguna parte del documento llega a un registro. Son escaneos del código (ADR-009).
/// </summary>
public class QuotePdfArchitectureTests
{
    private const string Endpoints = "src/OfiFlow.Api/Endpoints/QuoteEndpoints.cs";
    private const string Handler = "OfiFlow.Application/Quotes/Queries/GetQuotePdf/GetQuotePdfQueryHandler.cs";

    private static string Code(string text) =>
        string.Join('\n', text.Split('\n').Select(line => line.TrimStart()).Where(line => !line.StartsWith("//", StringComparison.Ordinal)));

    private static string PdfEndpoint()
    {
        var code = Code(SourceCode.ReadText(Endpoints));
        var start = code.IndexOf("group.MapGet(\"/{id:guid}/pdf\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "No se encuentra el endpoint del PDF.");
        var end = code.IndexOf("group.Map", start + 10, StringComparison.Ordinal);

        return code[start..end];
    }

    // ===== La librería de PDF vive solo en Infrastructure =====

    [Fact]
    public void NoFileOutsideInfrastructureMentionsThePdfLibrary()
    {
        var offenders = SourceCode.Files()
            .Where(file => !file.Path.StartsWith("src/OfiFlow.Infrastructure/", StringComparison.Ordinal))
            .Where(file => Code(file.Content).Split('\n').Any(line => line.Contains("PdfSharp", StringComparison.Ordinal) || line.Contains("MigraDoc", StringComparison.Ordinal)))
            .Select(file => file.Path)
            .ToList();

        Assert.True(offenders.Count == 0, $"La librería del PDF (PDFsharp, MigraDoc) no sale de Infrastructure; Application solo conoce el puerto IQuotePdfRenderer: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void OnlyTheDocumentsFolderOfInfrastructureUsesThePdfLibrary()
    {
        var users = SourceCode.Files("OfiFlow.Infrastructure")
            .Where(file => Code(file.Content).Split('\n').Any(line => line.StartsWith("using PdfSharp", StringComparison.Ordinal) || line.StartsWith("using MigraDoc", StringComparison.Ordinal)))
            .Select(file => file.Path)
            .ToList();

        Assert.NotEmpty(users);
        Assert.All(users, path => Assert.StartsWith("src/OfiFlow.Infrastructure/Documents/", path, StringComparison.Ordinal));
    }

    [Fact]
    public void TheApplicationAndTheApiProjectsDoNotReferenceThePdfPackages()
    {
        foreach (var project in new[] { "src/OfiFlow.Application/OfiFlow.Application.csproj", "src/OfiFlow.Api/OfiFlow.Api.csproj", "src/OfiFlow.Domain/OfiFlow.Domain.csproj" })
        {
            var text = SourceCode.ReadText(project);
            Assert.DoesNotContain("PDFsharp", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MigraDoc", text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("PDFsharp-MigraDoc", SourceCode.ReadText("src/OfiFlow.Infrastructure/OfiFlow.Infrastructure.csproj"), StringComparison.Ordinal);
    }

    // ===== El dinero no pasa por coma flotante =====

    [Fact]
    public void TheMoneyFormatUsesOnlyIntegerArithmetic()
    {
        var code = Code(SourceCode.ReadText("src/OfiFlow.Infrastructure/Documents/MoneyFormat.cs"));

        Assert.False(Regex.IsMatch(code, @"\b(double|float|decimal)\b"), "MoneyFormat da formato al dinero desde céntimos enteros: ni double, ni float, ni decimal (spec 025 R5).");
        Assert.DoesNotContain("Math.Round", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CultureInfo.CurrentCulture", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRendererNeverComputesAnAmount()
    {
        // El generador imprime lo que ya calculó el servidor (R5): ninguna suma, producto ni redondeo de importes en él.
        var code = Code(SourceCode.ReadText("src/OfiFlow.Infrastructure/Documents/PdfSharpQuotePdfRenderer.cs"));

        foreach (var forbidden in new[] { ".Sum(", "Math.Round", "decimal.Round", "MidpointRounding", "Math.Floor", "Math.Ceiling", "Cents *", "Cents /", "Cents +", "Cents -", "Cents %" })
        {
            Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheModelFactoryNeverComputesAnAmountEither()
    {
        var code = Code(SourceCode.ReadText("src/OfiFlow.Application/Quotes/Pdf/QuotePdfModelFactory.cs"));

        foreach (var forbidden in new[] { ".Sum(", "Math.Round", "decimal.Round", "MidpointRounding", "Cents *", "Cents /", "Cents +", "Cents -", "Cents %", "QuoteCalculator" })
        {
            Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
        }
    }

    // ===== El nombre del fichero y las cabeceras =====

    [Fact]
    public void TheFileNameComesFromTheNumberAlone()
    {
        var handler = Code(SourceCode.ReadText("src/OfiFlow.Application/Quotes/Queries/GetQuotePdf/GetQuotePdfQueryHandler.cs"));

        Assert.Contains("QuoteLabel.FileName(quote.Number)", handler, StringComparison.Ordinal);

        var endpoint = PdfEndpoint();
        Assert.Contains("file.FileName", endpoint, StringComparison.Ordinal);
        foreach (var freeText in new[] { "Title", "CustomerName", "Notes", "Description", "LegalName" })
        {
            Assert.DoesNotContain(freeText, endpoint, StringComparison.Ordinal);
        }

        // Y el helper que lo construye solo recibe un entero.
        var label = Code(SourceCode.ReadText("src/OfiFlow.Application/Quotes/Pdf/QuoteLabel.cs"));
        Assert.Contains("public static string FileName(int number)", label, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEndpointIsAnAttachmentOfTypePdfThatIsNeverCached()
    {
        var endpoint = PdfEndpoint();

        Assert.Contains("Headers.CacheControl = \"no-store\"", endpoint, StringComparison.Ordinal);
        Assert.Contains("Results.File(file.Content, \"application/pdf\", file.FileName)", endpoint, StringComparison.Ordinal);
        // El no-store va antes de enviar la consulta: también los errores (400, 404) salen sin guardarse en cachés.
        Assert.True(endpoint.IndexOf("no-store", StringComparison.Ordinal) < endpoint.IndexOf("sender.Send", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDownloadHasItsOwnRateLimit_AndNeverTakesAnythingFromTheRequestButTheId()
    {
        var endpoint = PdfEndpoint();

        Assert.Contains(".RequireRateLimiting(RateLimiting.QuotePdf)", endpoint, StringComparison.Ordinal);
        Assert.Contains("(Guid id, HttpContext httpContext, ISender sender, CancellationToken cancellationToken)", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("tenantId", endpoint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[FromBody]", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("[FromQuery]", endpoint, StringComparison.Ordinal);

        var limits = Code(SourceCode.ReadText("src/OfiFlow.Api/Common/RateLimiting.cs"));
        Assert.Contains("public const string QuotePdf = \"quote-pdf\";", limits, StringComparison.Ordinal);
        Assert.Contains("options.AddPolicy(QuotePdf, context => PerIp(context, permitLimit: 30, TimeSpan.FromMinutes(1)));", limits, StringComparison.Ordinal);
    }

    // ===== Aislamiento y registros =====

    [Fact]
    public void ThePdfHandlerReadsOnlyThroughTheTenantFilter_AndNeverWrites()
    {
        var handler = SourceCode.Files("OfiFlow.Application/Quotes/Queries/GetQuotePdf").Single(f => f.Path.EndsWith("Handler.cs", StringComparison.Ordinal));
        var code = Code(handler.Content);

        Assert.DoesNotContain("IgnoreQueryFilters", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveChangesAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("TenantId", code, StringComparison.Ordinal); // la empresa sale del filtro global, nunca se pasa a mano
        Assert.DoesNotContain(".Add(", code, StringComparison.Ordinal);
        Assert.DoesNotContain(".Remove(", code, StringComparison.Ordinal);

        // Todas las lecturas sin seguimiento.
        foreach (var read in new[] { "dbContext.Quotes", "dbContext.CompanyProfiles", "dbContext.Customers" })
        {
            Assert.Contains($"{read}.AsNoTracking()", code, StringComparison.Ordinal);
        }

        Assert.EndsWith(Handler, handler.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingOfTheDocumentCanReachTheLogs()
    {
        // Ni el modelo, ni la consulta, ni el generador, ni su formato escriben en el registro: el PDF junta datos fiscales, un cliente y precios.
        var files = SourceCode.Files("OfiFlow.Infrastructure/Documents").Concat(SourceCode.Files("OfiFlow.Application/Quotes/Pdf")).Concat(SourceCode.Files("OfiFlow.Application/Quotes/Queries/GetQuotePdf")).ToList();
        Assert.True(files.Count >= 12, "El escaneo tiene que cubrir todos los ficheros del documento.");

        foreach (var file in files)
        {
            var code = Code(file.Content);
            foreach (var way in new[] { "ILogger", "LoggerMessage", "Console.Write", "Debug.Write", "Trace.Write", "Log.", "LogInformation", "LogWarning", "LogError" })
            {
                // El generador de arranque (QuotePdfStartupCheck) no tiene datos de nadie, pero tampoco escribe: avisa lanzando una excepción.
                Assert.False(code.Contains(way, StringComparison.Ordinal), $"{file.Path} escribe en el registro ({way}): el documento lleva datos que no se registran.");
            }
        }

        var endpoint = PdfEndpoint();
        foreach (var way in new[] { "ILogger", "LoggerMessage", "Console.Write", "Log." })
        {
            Assert.DoesNotContain(way, endpoint, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheDocumentCodeNeverTouchesTheFileSystemOrTheNetwork()
    {
        // El documento se genera en memoria a partir de datos del servidor: ni lee ni escribe ficheros (las fuentes son un recurso del ensamblado) ni abre conexiones.
        foreach (var file in SourceCode.Files("OfiFlow.Infrastructure/Documents"))
        {
            var code = Code(file.Content);
            foreach (var forbidden in new[] { "File.", "Directory.", "FileStream", "StreamWriter", "StreamReader", "Path.GetTempPath", "HttpClient", "WebClient", "Process.Start", "Environment.GetFolderPath", "Assembly.Load" })
            {
                Assert.False(code.Contains(forbidden, StringComparison.Ordinal), $"{file.Path} usa {forbidden}: el PDF se genera solo en memoria.");
            }
        }
    }

    [Fact]
    public void TheDocumentCodeNeverAddsLinksActionsOrAttachments()
    {
        // El PDF es inerte (R9): ninguna API de la librería que cree enlaces, acciones, adjuntos, formularios ni JavaScript.
        foreach (var file in SourceCode.Files("OfiFlow.Infrastructure/Documents"))
        {
            var code = Code(file.Content);
            foreach (var forbidden in new[] { "AddHyperlink", "AddBookmark", "AddWebLink", "AddDocumentLink", "AddFileLink", "Hyperlink", "AddAcroForm", "AcroForm", "JavaScript", "OpenAction", "EmbeddedFile", "Annotation", "AddAttachment", "SetPassword", "SecuritySettings" })
            {
                Assert.False(code.Contains(forbidden, StringComparison.Ordinal), $"{file.Path} usa {forbidden}: el PDF no lleva enlaces, acciones, adjuntos ni formularios.");
            }
        }
    }

    // ===== Operación y puerta del segundo factor =====

    [Fact]
    public void ThePdfOperationIsNotAllowedWithoutTheSecondFactor()
    {
        var files = SourceCode.Files("OfiFlow.Application/Quotes/Queries/GetQuotePdf").ToList();

        Assert.True(files.Count >= 3, "Faltan ficheros de la consulta del PDF.");
        Assert.All(files, file => Assert.DoesNotContain("AllowedWithoutSecondFactor", file.Content, StringComparison.Ordinal));
        Assert.All(files, file => Assert.DoesNotContain("AllowAnonymousRequest", file.Content, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryTypeOfTheDocumentNamespaceStillOverridesToString()
    {
        // La consulta del PDF añadió el resultado (QuotePdfFile) al espacio de nombres: el escaneo de Application lo cubre, y aquí se comprueba que está dentro.
        var types = typeof(QuotePdfFile).Assembly.GetTypes().Where(t => t.Namespace == typeof(QuotePdfFile).Namespace && t.GetMethod("<Clone>$") is not null).Select(t => t.Name).ToList();

        Assert.Contains(nameof(QuotePdfFile), types);
        Assert.Contains(nameof(QuotePdfModel), types);
    }
}
