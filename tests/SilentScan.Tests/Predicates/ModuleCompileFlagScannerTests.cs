using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "Small precise adds" - WITH RECOMPILE (sys.sql_modules.is_recompiled)
/// and a non-schema-bound table-valued function's RETURNS TABLE column using database collation
/// (sys.sql_modules.uses_database_collation), both live-mode-only catalog flags, same test shape
/// as <see cref="SetOptionScannerTests"/>.
/// </summary>
public sealed class ModuleCompileFlagScannerTests
{
    private const string ModuleName = "dbo.usp_Test";

    private static IReadOnlyList<ModuleCompileFlagFinding> Scan(
        string sql, bool? isRecompiled = null, bool? usesDatabaseCollation = null, bool? isSchemaBound = null)
    {
        var catalog = new DatabaseCatalog();
        var result = SqlScriptParser.ParseText(ModuleName, sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        if (isRecompiled is { } recompiled)
        {
            catalog.AddModuleIsRecompiled(ModuleName, recompiled);
        }

        if (usesDatabaseCollation is { } udc)
        {
            catalog.AddModuleUsesDatabaseCollation(ModuleName, udc);
        }

        if (isSchemaBound is { } sb)
        {
            catalog.AddModuleIsSchemaBound(ModuleName, sb);
        }

        return ModuleCompileFlagScanner.Scan(result, catalog);
    }

    [Fact]
    public void IsRecompiledTrue_Fires()
    {
        var findings = Scan("CREATE PROCEDURE dbo.usp_Test WITH RECOMPILE AS BEGIN SELECT 1; END", isRecompiled: true);

        var finding = Assert.Single(findings);
        Assert.Equal(ModuleCompileFlagFindingKind.RecompilesEveryCall, finding.Kind);
        Assert.Equal(ModuleName, finding.ModuleQualifiedName);
    }

    [Fact]
    public void IsRecompiledFalse_DoesNotFire()
    {
        var findings = Scan("CREATE PROCEDURE dbo.usp_Test AS BEGIN SELECT 1; END", isRecompiled: false);

        Assert.Empty(findings);
    }

    [Fact]
    public void IsRecompiledUnknown_NeverGuessesFire()
    {
        // Flag never registered at all (as in a file-mode scan) - must never be treated as true.
        var findings = Scan("CREATE PROCEDURE dbo.usp_Test AS BEGIN SELECT 1; END");

        Assert.Empty(findings);
    }

    [Fact]
    public void UsesDatabaseCollationTrue_NonSchemaBound_Fires()
    {
        var findings = Scan(
            "CREATE FUNCTION dbo.usp_Test() RETURNS @t TABLE (Val VARCHAR(50)) AS BEGIN RETURN; END",
            usesDatabaseCollation: true, isSchemaBound: false);

        var finding = Assert.Single(findings);
        Assert.Equal(ModuleCompileFlagFindingKind.TableValuedFunctionReturnUsesDatabaseCollation, finding.Kind);
    }

    [Fact]
    public void UsesDatabaseCollationTrue_SchemaBound_DoesNotFire()
    {
        // Oracle-confirmed 2026-08-17: schema-binding sets uses_database_collation
        // unconditionally, string data or not - excluded here as a redundant, always-true,
        // zero-information signal for a schema-bound module (see
        // ModuleCompileFlagFinding's own doc comment for the full oracle write-up).
        var findings = Scan(
            "CREATE FUNCTION dbo.usp_Test() RETURNS @t TABLE (Val VARCHAR(50)) AS BEGIN RETURN; END",
            usesDatabaseCollation: true, isSchemaBound: true);

        Assert.Empty(findings);
    }

    [Fact]
    public void UsesDatabaseCollationFalse_DoesNotFire()
    {
        var findings = Scan(
            "CREATE FUNCTION dbo.usp_Test() RETURNS @t TABLE (Val VARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS) AS BEGIN RETURN; END",
            usesDatabaseCollation: false, isSchemaBound: false);

        Assert.Empty(findings);
    }

    [Fact]
    public void BothFlagsTrue_FiresBothIndependently()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Test WITH RECOMPILE AS BEGIN SELECT 1; END",
            isRecompiled: true, usesDatabaseCollation: true, isSchemaBound: false);

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.Kind == ModuleCompileFlagFindingKind.RecompilesEveryCall);
        Assert.Contains(findings, f => f.Kind == ModuleCompileFlagFindingKind.TableValuedFunctionReturnUsesDatabaseCollation);
    }
}
