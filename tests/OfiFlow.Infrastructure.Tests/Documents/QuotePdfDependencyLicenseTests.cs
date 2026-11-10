using System.Xml.Linq;
using PdfSharp.Pdf;

namespace OfiFlow.Infrastructure.Tests.Documents;

/// <summary>
/// La licencia de las librerías del PDF (spec 025 D2, ADR-010). PDFsharp y MigraDoc son MIT <b>hasta la 6.2.4</b>; la 6.5.0 y la 7.0.0-preview-2 (14-10-2026) pasaron a AGPL-3.0, que obligaría a
/// publicar el código de todo lo que se enlace con ellas. Estos tests leen el <c>.nuspec</c> de lo que se restauró de verdad y fallan si una actualización cambia la versión o la licencia.
/// </summary>
public class QuotePdfDependencyLicenseTests
{
    private const string PinnedVersion = "6.2.4";

    private static string PackagesFolder()
    {
        // El mismo orden que usa NuGet: la variable de entorno y, si no, la carpeta del perfil de usuario.
        var fromEnvironment = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    }

    private static XDocument Nuspec(string id, string version)
    {
        var path = Path.Combine(PackagesFolder(), id.ToLowerInvariant(), version, id.ToLowerInvariant() + ".nuspec");
        Assert.True(File.Exists(path), $"No se encuentra el .nuspec de {id} {version} en la caché de NuGet ({path}).");

        return XDocument.Load(path);
    }

    private static string? LicenseOf(XDocument nuspec) =>
        nuspec.Descendants().FirstOrDefault(e => e.Name.LocalName == "license")?.Value;

    private static string? VersionOf(XDocument nuspec) =>
        nuspec.Descendants().FirstOrDefault(e => e.Name.LocalName == "version")?.Value;

    [Theory]
    [InlineData("PDFsharp")]
    [InlineData("PDFsharp-MigraDoc")]
    public void EachLibraryOfThePdfIsMitAtThePinnedVersion(string id)
    {
        var nuspec = Nuspec(id, PinnedVersion);

        Assert.Equal(PinnedVersion, VersionOf(nuspec));
        Assert.Equal("MIT", LicenseOf(nuspec));
    }

    [Fact]
    public void TheAssemblyInUseIsTheOneOfThePinnedVersion()
    {
        var version = typeof(PdfDocument).Assembly.GetName().Version!;

        // 6.2.4 → ensamblado 6.2.x. Una actualización que subiera la versión menor lo notaría aquí además de en el .nuspec.
        Assert.Equal((6, 2), (version.Major, version.Minor));
    }

    [Fact]
    public void TheProjectFilePinsTheLibraryToTheMitVersion()
    {
        var csproj = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "OfiFlow.Infrastructure", "OfiFlow.Infrastructure.csproj"));

        Assert.Contains($"Include=\"PDFsharp-MigraDoc\" Version=\"{PinnedVersion}\"", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void DependabotIsToldNotToProposeTheAgplVersions()
    {
        var dependabot = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "dependabot.yml"));

        Assert.Contains("dependency-name: \"PDFsharp\"", dependabot, StringComparison.Ordinal);
        Assert.Contains("dependency-name: \"PDFsharp-MigraDoc\"", dependabot, StringComparison.Ordinal);
        Assert.Contains("AGPL", dependabot, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLicenseReaderDoesNotTakeAnAgplNuspecForMit()
    {
        // Control del lector de .nuspec: con una licencia AGPL (la de la 6.5.0) no devuelve MIT ni la versión fijada, así que las comprobaciones de arriba fallarían.
        var agpl = XDocument.Parse("<package><metadata><version>6.5.0</version><license type='expression'>AGPL-3.0-only</license></metadata></package>");

        Assert.Equal("AGPL-3.0-only", LicenseOf(agpl));
        Assert.NotEqual("MIT", LicenseOf(agpl));
        Assert.NotEqual(PinnedVersion, VersionOf(agpl));
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
