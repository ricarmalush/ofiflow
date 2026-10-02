namespace OfiFlow.Api.Tests.Architecture;

/// <summary>
/// Búsqueda de texto en el código fuente para los tests de arquitectura. Es un escaneo simple,
/// sin compilar: rápido y sin dependencias, a cambio de algún falso positivo aceptado (ADR-009).
/// </summary>
internal static class SourceCode
{
    /// <summary>Ficheros .cs bajo <c>src/{subfolder}</c> que contienen el texto, con ruta relativa a la raíz.</summary>
    public static List<string> FilesContaining(string text, string subfolder = "") =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src", subfolder), "*.cs", SearchOption.AllDirectories)
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
