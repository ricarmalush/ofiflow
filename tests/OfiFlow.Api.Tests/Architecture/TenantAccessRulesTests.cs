namespace OfiFlow.Api.Tests.Architecture;

/// <summary>
/// Comprueba con ficheros sintéticos que las reglas de <see cref="TenantAccessRules"/> detectan lo
/// que dicen detectar (spec 007): un guardarraíl que nunca ha fallado no demuestra que funcione.
/// </summary>
public class TenantAccessRulesTests
{
    private const string AllowedPath = "src/OfiFlow.Infrastructure/Identity/TokenService.cs";

    private static readonly Dictionary<string, string[]> ReadAllowList = new()
    {
        ["Users"] = [],
        ["Tenants"] = [],
        ["RefreshTokens"] = [AllowedPath]
    };

    private static SourceFile Src(string path, string content) => new(path, content);

    // --- IgnoreQueryFilters ---

    [Fact]
    public void IgnoreQueryFilters_InAnAllowedPath_IsAccepted()
    {
        var files = new[] { Src(AllowedPath, "db.TenantUsers.IgnoreQueryFilters()") };

        Assert.Empty(TenantAccessRules.IgnoreQueryFiltersOffenders(files, [AllowedPath]));
    }

    [Fact]
    public void IgnoreQueryFilters_InAFileWithTheSameNameButAnotherPath_IsRejected()
    {
        const string impostor = "src/OfiFlow.Application/Jobs/TokenService.cs";
        var files = new[] { Src(impostor, "db.Jobs.IgnoreQueryFilters()") };

        var offenders = TenantAccessRules.IgnoreQueryFiltersOffenders(files, [AllowedPath]);

        Assert.Equal([impostor], offenders);
    }

    [Fact]
    public void IgnoreQueryFilters_NotUsed_IsAccepted()
    {
        var files = new[] { Src("src/OfiFlow.Application/Jobs/X.cs", "db.Jobs.ToList()") };

        Assert.Empty(TenantAccessRules.IgnoreQueryFiltersOffenders(files, [AllowedPath]));
    }

    // --- Entidades globales ---

    [Theory]
    [InlineData("var all = await dbContext.Users.ToListAsync();")]
    [InlineData("var all = dbContext.Tenants.Where(t => t.Name == x);")]
    [InlineData("var user = await dbContext.Users.FirstAsync(u => u.Id == id);")]
    public void ReadingAGlobalDbSet_OutsideTheAllowList_IsRejected(string code)
    {
        var files = new[] { Src("src/OfiFlow.Application/Jobs/Leak.cs", code) };

        var offenders = TenantAccessRules.GlobalEntityAccessOffenders(files, ReadAllowList);

        Assert.Single(offenders);
        Assert.StartsWith("src/OfiFlow.Application/Jobs/Leak.cs:1:", offenders[0]);
    }

    [Fact]
    public void ReadingAGlobalDbSet_OnAnotherLine_ReportsTheRightLine()
    {
        var content = "var a = 1;\n\nvar all = dbContext.Users.ToList();\n";
        var files = new[] { Src("src/OfiFlow.Application/Jobs/Leak.cs", content) };

        var offenders = TenantAccessRules.GlobalEntityAccessOffenders(files, ReadAllowList);

        Assert.Equal(["src/OfiFlow.Application/Jobs/Leak.cs:3: .Users"], offenders);
    }

    [Fact]
    public void ReadingAGlobalDbSet_AcrossLines_IsRejected()
    {
        var content = "var all = await dbContext.Users\n    .Where(u => u.Name == x)\n    .ToListAsync();";
        var files = new[] { Src("src/OfiFlow.Application/Jobs/Leak.cs", content) };

        Assert.NotEmpty(TenantAccessRules.GlobalEntityAccessOffenders(files, ReadAllowList));
    }

    [Theory]
    [InlineData("dbContext.Users.Add(user);")]
    [InlineData("dbContext.Tenants.Add(tenant);")]
    [InlineData("await dbContext.Users.AddAsync(user, ct);")]
    public void CreatingInAGlobalDbSet_IsAcceptedAnywhere(string code)
    {
        var files = new[] { Src("src/OfiFlow.Application/Identity/Anywhere.cs", code) };

        Assert.Empty(TenantAccessRules.GlobalEntityAccessOffenders(files, ReadAllowList));
    }

    [Fact]
    public void ReadingAGlobalDbSet_InAnAllowedPath_IsAccepted()
    {
        var files = new[] { Src(AllowedPath, "var t = await dbContext.RefreshTokens.FirstOrDefaultAsync(x => x.TokenHash == h);") };

        Assert.Empty(TenantAccessRules.GlobalEntityAccessOffenders(files, ReadAllowList));
    }

    [Fact]
    public void ReadingAGlobalDbSet_InAFileWithTheSameNameButAnotherPath_IsRejected()
    {
        var files = new[] { Src("src/OfiFlow.Application/Other/TokenService.cs", "dbContext.RefreshTokens.ToList();") };

        Assert.NotEmpty(TenantAccessRules.GlobalEntityAccessOffenders(files, ReadAllowList));
    }

    [Fact]
    public void AMentionInAComment_IsNotAnAccess()
    {
        var content = "// No se lee dbContext.Users aquí: no tiene filtro de tenant.\n/// dbContext.Tenants tampoco.";
        var files = new[] { Src("src/OfiFlow.Application/Jobs/Doc.cs", content) };

        Assert.Empty(TenantAccessRules.GlobalEntityAccessOffenders(files, ReadAllowList));
    }

    [Fact]
    public void ADbSetOfATenantOwnedEntity_IsNotAffected()
    {
        var files = new[] { Src("src/OfiFlow.Application/Jobs/Ok.cs", "dbContext.Jobs.ToList(); dbContext.TenantUsers.ToList();") };

        Assert.Empty(TenantAccessRules.GlobalEntityAccessOffenders(files, ReadAllowList));
    }
}
