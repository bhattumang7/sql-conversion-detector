using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class ModuleCompileFlagPipelineTests
{
    [Fact]
    public async Task ProcedureWithRecompile_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync("""
            CREATE PROCEDURE dbo.usp_Recompiled WITH RECOMPILE AS
            BEGIN
                SELECT 1 AS X;
            END
            """);

        var finding = Assert.Single(report.Find<ModuleCompileFlagFinding>("ModuleCompileFlagScanner"), f => f.Kind == ModuleCompileFlagFindingKind.RecompilesEveryCall);
        Assert.Equal("dbo.usp_Recompiled", finding.ModuleQualifiedName);
    }

    [Fact]
    public async Task ProcedureWithoutRecompile_DoesNotFire()
    {
        var report = await EngineAuthoritativeScan.ScanAsync("""
            CREATE PROCEDURE dbo.usp_Plain AS
            BEGIN
                SELECT 1 AS X;
            END
            """);

        Assert.DoesNotContain(report.Find<ModuleCompileFlagFinding>("ModuleCompileFlagScanner"), f => f.Kind == ModuleCompileFlagFindingKind.RecompilesEveryCall);
    }

    [Fact]
    public async Task TableValuedFunctionReturnsUncollatedStringColumn_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync("""
            CREATE FUNCTION dbo.fn_Names()
            RETURNS @t TABLE (Val VARCHAR(50))
            AS
            BEGIN
                INSERT INTO @t VALUES ('a');
                RETURN;
            END
            """);

        var finding = Assert.Single(report.Find<ModuleCompileFlagFinding>("ModuleCompileFlagScanner"), f => f.Kind == ModuleCompileFlagFindingKind.TableValuedFunctionReturnUsesDatabaseCollation);
        Assert.Equal("dbo.fn_Names", finding.ModuleQualifiedName);
    }

    [Fact]
    public async Task TableValuedFunctionReturnsExplicitlyCollatedStringColumn_DoesNotFire()
    {
        var report = await EngineAuthoritativeScan.ScanAsync("""
            CREATE FUNCTION dbo.fn_NamesCollated()
            RETURNS @t TABLE (Val VARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS)
            AS
            BEGIN
                INSERT INTO @t VALUES ('a');
                RETURN;
            END
            """);

        Assert.DoesNotContain(report.Find<ModuleCompileFlagFinding>("ModuleCompileFlagScanner"), f => f.Kind == ModuleCompileFlagFindingKind.TableValuedFunctionReturnUsesDatabaseCollation);
    }

    [Fact]
    public async Task TableValuedFunctionReturnsIntOnly_DoesNotFire()
    {
        var report = await EngineAuthoritativeScan.ScanAsync("""
            CREATE FUNCTION dbo.fn_IntsOnly()
            RETURNS @t TABLE (Val INT)
            AS
            BEGIN
                INSERT INTO @t VALUES (1);
                RETURN;
            END
            """);

        Assert.DoesNotContain(report.Find<ModuleCompileFlagFinding>("ModuleCompileFlagScanner"), f => f.Kind == ModuleCompileFlagFindingKind.TableValuedFunctionReturnUsesDatabaseCollation);
    }

    [Fact]
    public async Task SchemaBoundFunction_ExcludedDespiteUsesDatabaseCollationFlag()
    {

        var report = await EngineAuthoritativeScan.ScanAsync("""
            CREATE FUNCTION dbo.fn_PureMath(@x INT)
            RETURNS INT
            WITH SCHEMABINDING
            AS
            BEGIN
                RETURN @x + 1;
            END
            """);

        Assert.DoesNotContain(report.Find<ModuleCompileFlagFinding>("ModuleCompileFlagScanner"), f => f.Kind == ModuleCompileFlagFindingKind.TableValuedFunctionReturnUsesDatabaseCollation);
    }
}
