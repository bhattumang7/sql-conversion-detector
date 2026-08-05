using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;

namespace SilentScan.Tests.Predicates.DynamicSqlValue;

/// <summary>
/// Exercises <see cref="DynamicSqlScannerV2.Scan"/> - the new engine's top-level entry point,
/// matching <see cref="DynamicSqlScanner.Scan"/>'s own signature so a parity harness can compare
/// both against the same input (docs/dynamic-sql-rebuild-plan.md Phase 3's exit gate). Covers
/// what <see cref="DynamicSqlTransferTests"/> does not: multi-batch scripts, nested CREATE
/// PROCEDURE/TRIGGER scope discovery and DynamicSqlScope propagation, and OUTPUT-parameter
/// summary recording end to end.
/// </summary>
public sealed class DynamicSqlScannerV2Tests
{
    private const string SourcePath = "test.sql";

    private static SqlParseResult Parse(string sql)
    {
        var result = SqlScriptParser.ParseText(SourcePath, sql);
        Assert.False(result.HasErrors, string.Join(';', result.Errors.Select(e => e.Message)));
        return result;
    }

    [Fact]
    public void TopLevelExec_OutsideAnyProcedure_EmitsScriptWithNoScope()
    {
        var result = DynamicSqlScannerV2.Scan(Parse("EXEC('SELECT 1');"));

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
        Assert.Null(script.Scope.ProcScope);
    }

    [Fact]
    public void ExecInsideCreateProcedure_RecordsProcedureAsScope()
    {
        var result = DynamicSqlScannerV2.Scan(Parse(
            "CREATE PROCEDURE dbo.usp_Test AS " +
            "BEGIN " +
            "EXEC('SELECT 1'); " +
            "END;"));

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("dbo.usp_Test", script.Scope.ProcScope);
    }

    [Fact]
    public void StubThenAlterProcedurePattern_WalksTheAlteredBody()
    {
        // A real-world corpus shape (First Responder Kit): a stub CREATE PROCEDURE followed by
        // ALTER PROCEDURE carrying the real body - matching on the shared
        // ProcedureStatementBodyBase base (not the concrete CREATE-only type) is what catches it.
        var result = DynamicSqlScannerV2.Scan(Parse(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN RETURN 0; END;\nGO\n" +
            "ALTER PROCEDURE dbo.usp_Test AS BEGIN EXEC('SELECT 1'); END;"));

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("dbo.usp_Test", script.Scope.ProcScope);
    }

    [Fact]
    public void ExecInsideTrigger_RecordsTriggerNameAndTargetTable()
    {
        var result = DynamicSqlScannerV2.Scan(Parse(
            "CREATE TRIGGER dbo.trg_Test ON dbo.Orders AFTER INSERT AS " +
            "BEGIN " +
            "EXEC('SELECT 1'); " +
            "END;"));

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("dbo.trg_Test", script.Scope.ProcScope);
        Assert.NotNull(script.Scope.TriggerTarget);
    }

    [Fact]
    public void UnseededFormalParameter_ReportsVariableNotInScope()
    {
        // No call-graph wiring yet (deferred - see DynamicSqlScannerV2's own doc comment) - a
        // formal parameter reference behaves exactly like the old scanner's own "no call graph
        // supplied" fallback: unseeded, reported as an ordinary unresolved variable.
        var result = DynamicSqlScannerV2.Scan(Parse(
            "CREATE PROCEDURE dbo.usp_Test @Status NVARCHAR(20) AS " +
            "BEGIN " +
            "DECLARE @sql NVARCHAR(MAX) = 'SELECT ' + @Status; " +
            "EXEC(@sql); " +
            "END;"));

        Assert.Empty(result.AnalyzableScripts);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("variable-not-in-scope", finding.Reason);
    }

    [Fact]
    public void OutputParameter_ProvenConstant_RecordsSummary()
    {
        var result = DynamicSqlScannerV2.Scan(Parse(
            "CREATE PROCEDURE dbo.usp_Test @Result NVARCHAR(50) OUTPUT AS " +
            "BEGIN " +
            "SET @Result = 'fixed-value'; " +
            "END;"));

        var summary = Assert.Single(result.OutputSummaries);
        Assert.Equal("dbo.usp_Test", summary.QualifiedName);
        Assert.Equal("@Result", summary.ParameterName);
        Assert.Equal(["fixed-value"], summary.PossibleValues);
    }

    [Fact]
    public void OutputParameter_RestingOnHole_RecordsNoSummary()
    {
        var result = DynamicSqlScannerV2.Scan(Parse(
            "CREATE PROCEDURE dbo.usp_Test @Result NVARCHAR(50) OUTPUT AS " +
            "BEGIN " +
            "DECLARE @unknown NVARCHAR(50); " +
            "SET @Result = @unknown; " +
            "END;"));

        Assert.Empty(result.OutputSummaries);
    }

    [Fact]
    public void NestedProcedureScope_GetsFreshDeclaredTypesNotLeakedFromOuter()
    {
        // The outer batch declares @x as INT; the nested procedure declares its OWN @x as
        // NVARCHAR - the two must never be conflated (a fresh DeclaredTypes dict per scope).
        var result = DynamicSqlScannerV2.Scan(Parse(
            "DECLARE @x INT = 1;\nGO\n" +
            "CREATE PROCEDURE dbo.usp_Test AS " +
            "BEGIN " +
            "DECLARE @x NVARCHAR(50) = 'inner'; " +
            "EXEC('SELECT ' + @x); " +
            "END;"));

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT inner", script.InnerText);
    }
}
