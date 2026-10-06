using System.Text.RegularExpressions;

namespace OfiFlow.Api.Tests.Architecture;

/// <summary>
/// Reglas de código fuente sobre el aislamiento entre empresas (spec 007, ADR-009 R2). Son
/// funciones puras (reciben ficheros, devuelven incumplimientos) para poder probar con ficheros
/// sintéticos que realmente detectan lo que dicen detectar.
/// </summary>
internal static class TenantAccessRules
{
    /// <summary>
    /// Ficheros, por ruta relativa, que usan <c>IgnoreQueryFilters(</c> sin estar en la lista blanca.
    /// Se compara la ruta completa y no el nombre del fichero: un fichero con el mismo nombre en otra
    /// carpeta no hereda el permiso.
    /// </summary>
    public static IReadOnlyList<string> IgnoreQueryFiltersOffenders(IEnumerable<SourceFile> files, IReadOnlyCollection<string> allowList) =>
        files
            .Where(file => file.Content.Contains("IgnoreQueryFilters(", StringComparison.Ordinal))
            .Select(file => file.Path)
            .Where(path => !allowList.Contains(path))
            .ToList();

    /// <summary>
    /// Accesos a un DbSet de entidad global (sin filtro de tenant) fuera de los ficheros permitidos
    /// para cada uno. Un <c>.Add(</c> o <c>.AddAsync(</c> se admite en cualquier fichero: crear un
    /// dato no expone los de otras empresas; lo que expone es leer. Cada incumplimiento se devuelve
    /// como <c>ruta:línea: .DbSet</c>.
    /// </summary>
    /// <param name="readAllowList">Para cada DbSet global, los ficheros (ruta relativa) que pueden leerlo.</param>
    public static IReadOnlyList<string> GlobalEntityAccessOffenders(
        IEnumerable<SourceFile> files,
        IReadOnlyDictionary<string, string[]> readAllowList)
    {
        var dbSets = string.Join("|", readAllowList.Keys.Select(Regex.Escape));
        var access = new Regex($@"\.(?<set>{dbSets})\b(?!\s*\.\s*Add(Async)?\()", RegexOptions.CultureInvariant);

        var offenders = new List<string>();

        foreach (var file in files)
        {
            var lines = file.Content.Split('\n');

            for (var index = 0; index < lines.Length; index++)
            {
                if (lines[index].TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match match in access.Matches(lines[index]))
                {
                    var dbSet = match.Groups["set"].Value;

                    if (!readAllowList[dbSet].Contains(file.Path))
                    {
                        offenders.Add($"{file.Path}:{index + 1}: .{dbSet}");
                    }
                }
            }
        }

        return offenders;
    }
}
