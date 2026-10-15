using System.Text.Json;

namespace OfiFlow.Api.Tests.Architecture;

/// <summary>
/// Guardarraíles de ADR-015 (logs a fichero): reglas que, si se rompen, no fallan a la vista de nadie.
/// </summary>
public class LoggingConfigurationTests
{
    private const string ApiFolder = "OfiFlow.Api";

    [Fact]
    public void EveryFileSink_UsesAJsonFormatter()
    {
        // Un fichero de texto plano permitiría falsificar entradas: la ruta de la petición la elige quien
        // llama y puede traer un salto de línea (%0A) seguido de una línea de log inventada. En JSON ese
        // salto queda escapado dentro del mismo registro.
        var fileSinks = FileSinks().ToList();

        Assert.NotEmpty(fileSinks); // control positivo: si nadie escribe a fichero, la regla no comprobaría nada
        foreach (var sink in fileSinks)
        {
            Assert.True(
                sink.Formatter is not null && sink.Formatter.Contains("Json", StringComparison.Ordinal),
                $"{sink.File}: el destino de fichero de Serilog debe usar un formateador JSON (CompactJsonFormatter o RenderedCompactJsonFormatter).");
        }
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void EveryRealEnvironment_WritesLogsToAFile(string environment)
    {
        // Sin esto, producción se quedaría sin dónde mirar cuando algo falle. Los entornos de test no escriben.
        Assert.Contains(FileSinks(), sink => sink.File.EndsWith($"appsettings.{environment}.json", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLogFolder_IsIgnoredByGit()
    {
        // Los logs pueden contener Ids, IPs y trazas: no deben acabar en el repositorio.
        var lines = SourceCode.ReadText(".gitignore").Split('\n').Select(line => line.Trim());

        Assert.Contains("logs/", lines);
    }

    [Fact]
    public void SerilogIsAProvider_NotAReplacementOfTheLoggerFactory()
    {
        // UseSerilog sustituye el ILoggerFactory: los tests que añaden su propio proveedor dejarían de ver
        // los logs y los guardarraíles sobre eventos de seguridad pasarían en falso. Se integra con AddSerilog.
        Assert.Empty(SourceCode.FilesContaining(".UseSerilog("));
        Assert.Contains($"src/{ApiFolder}/Program.cs", SourceCode.FilesContaining(".AddSerilog(", ApiFolder));
    }

    private static IEnumerable<(string File, string? Formatter)> FileSinks()
    {
        foreach (var file in SourceCode.FilesMatching(ApiFolder, "appsettings*.json"))
        {
            using var document = JsonDocument.Parse(SourceCode.ReadText(file));

            if (!document.RootElement.TryGetProperty("Serilog", out var serilog) ||
                !serilog.TryGetProperty("WriteTo", out var writeTo))
            {
                continue;
            }

            foreach (var sink in writeTo.EnumerateArray())
            {
                if (sink.GetProperty("Name").GetString() != "File")
                {
                    continue;
                }

                string? formatter = null;
                if (sink.TryGetProperty("Args", out var args) && args.TryGetProperty("formatter", out var value))
                {
                    formatter = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
                }

                yield return (file, formatter);
            }
        }
    }
}
