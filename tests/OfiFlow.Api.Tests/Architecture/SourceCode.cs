namespace OfiFlow.Api.Tests.Architecture;

/// <summary>Un fichero .cs de <c>src</c>: ruta relativa a la raíz del repositorio (siempre con '/') y contenido.</summary>
internal sealed record SourceFile(string Path, string Content);

/// <summary>
/// Búsqueda de texto en el código fuente para los tests de arquitectura. Es un escaneo simple,
/// sin compilar: rápido y sin dependencias, a cambio de algún falso positivo aceptado (ADR-009).
/// </summary>
internal static class SourceCode
{
    /// <summary>Ficheros .cs bajo <c>src/{subfolder}</c>, sin código generado.</summary>
    public static IEnumerable<SourceFile> Files(string subfolder = "")
    {
        var root = RepositoryRoot();

        return Directory.EnumerateFiles(Path.Combine(root, "src", subfolder), "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsGenerated(file))
            .Select(file => new SourceFile(
                // Siempre con '/': la misma ruta en Windows y en el runner de Linux, para poder compararla.
                Path.GetRelativePath(root, file).Replace('\\', '/'),
                File.ReadAllText(file)));
    }

    /// <summary>Rutas relativas de los ficheros bajo <c>src/{subfolder}</c> que contienen el texto.</summary>
    public static List<string> FilesContaining(string text, string subfolder = "") =>
        Files(subfolder)
            .Where(file => file.Content.Contains(text, StringComparison.Ordinal))
            .Select(file => file.Path)
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
