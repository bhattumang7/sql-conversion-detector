using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "Small precise adds" - end-to-end oracle confirmation through the
/// real live pipeline (<see cref="EngineAuthoritativeScan"/>: deploy to the disposable Docker
/// instance, run the same <c>LiveScanRunner</c> a real <c>scan-db</c> target uses), proving
/// <c>sys.sql_modules.is_recompiled</c>/<c>uses_database_collation</c> are read correctly off a
/// REAL deployed module rather than only unit-tested against a hand-built catalog
/// (<see cref="ModuleCompileFlagScannerTests"/>).
///
/// The <c>uses_database_collation</c> scope itself (fires for a non-schema-bound TVF's own
/// un-COLLATE'd RETURNS TABLE string column; excluded entirely for a schema-bound module, since
/// schema-binding sets the underlying flag unconditionally regardless of string data - oracle-
/// discovered directly against the Docker instance, 2026-08-17) is the load-bearing claim these
/// tests lock in, matching the reasoning documented on <see cref="ModuleCompileFlagFinding"/>
/// itself.
/// </summary>
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

        var finding = Assert.Single(report.ModuleCompileFlagFindings, f => f.Kind == ModuleCompileFlagFindingKind.RecompilesEveryCall);
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

        Assert.DoesNotContain(report.ModuleCompileFlagFindings, f => f.Kind == ModuleCompileFlagFindingKind.RecompilesEveryCall);
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

        var finding = Assert.Single(report.ModuleCompileFlagFindings, f => f.Kind == ModuleCompileFlagFindingKind.TableValuedFunctionReturnUsesDatabaseCollation);
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

        Assert.DoesNotContain(report.ModuleCompileFlagFindings, f => f.Kind == ModuleCompileFlagFindingKind.TableValuedFunctionReturnUsesDatabaseCollation);
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

        Assert.DoesNotContain(report.ModuleCompileFlagFindings, f => f.Kind == ModuleCompileFlagFindingKind.TableValuedFunctionReturnUsesDatabaseCollation);
    }

    [Fact]
    public async Task SchemaBoundFunction_ExcludedDespiteUsesDatabaseCollationFlag()
    {
        // Oracle-confirmed: a schema-bound module sets uses_database_collation = 1
        // unconditionally, even with zero string columns anywhere - this finding must never
        // report on it, or it would be a redundant, always-true claim for every schema-bound
        // object in the database.
        var report = await EngineAuthoritativeScan.ScanAsync("""
            CREATE FUNCTION dbo.fn_PureMath(@x INT)
            RETURNS INT
            WITH SCHEMABINDING
            AS
            BEGIN
                RETURN @x + 1;
            END
            """);

        Assert.DoesNotContain(report.ModuleCompileFlagFindings, f => f.Kind == ModuleCompileFlagFindingKind.TableValuedFunctionReturnUsesDatabaseCollation);
    }
}
