using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Core.TypeInference;

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
        // No call graph passed at all (the common case for an isolated/unit-tested scan) - a
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

    /// <summary>
    /// A stored procedure is a public surface - the ONE in-corpus caller this scan found passing
    /// 'Active' does not mean every caller does (app code, jobs, EXEC-by-name calls are all
    /// invisible to this scan's own call graph). The seed widens to a choice between the known
    /// literal (still reported, High confidence) and an unresolved placeholder covering every
    /// other possible caller (Medium confidence) - never just the one literal asserted as ground
    /// truth.
    /// </summary>
    [Fact]
    public void FormalParameter_SingleKnownCallerWithLiteralArgument_SeedsChoiceOfLiteralAndExternalCallerPlaceholder()
    {
        var callGraph = new ProcCallGraph([
            new ProcCallEdge(
                CallerScopeQualifiedName: null, CalleeQualifiedName: "dbo.usp_Test", CallSite: new SourceSpan("caller.sql", 1, 1),
                Arguments: [new ProcCallArgument(
                    "@Status", new SqlType(SqlTypeCategory.NVarChar, Length: 20), FormalParameterIsOutput: false,
                    CallerVariableName: null, IsLiteral: true,
                    LiteralArgument: new ProcCallLiteralArgument("Active", "caller.sql", 1, 1, PrefixLength: 1))]),
        ]);

        var result = DynamicSqlScannerV2.Scan(
            Parse("CREATE PROCEDURE dbo.usp_Test @Status NVARCHAR(20) AS BEGIN EXEC('SELECT ' + @Status); END;"),
            callGraph: callGraph);

        Assert.Equal(2, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT Active" && s.Confidence == FindingConfidence.High);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText != "SELECT Active" && s.Confidence == FindingConfidence.Medium);
    }

    [Fact]
    public void FormalParameter_NoKnownCaller_SeedsTypedPlaceholderNotBareUnresolved()
    {
        var callGraph = new ProcCallGraph([]);

        var result = DynamicSqlScannerV2.Scan(
            Parse("CREATE PROCEDURE dbo.usp_Test @Status NVARCHAR(20) AS BEGIN EXEC('SELECT ' + @Status); END;"),
            callGraph: callGraph);

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
        Assert.NotNull(script.PlaceholderOccurrences);
    }

    [Fact]
    public void FormalParameter_MultipleCallersAllLiteral_SeedsTheAgreedSetPlusExternalCallerChoice()
    {
        var callGraph = new ProcCallGraph([
            new ProcCallEdge(null, "dbo.usp_Test", new SourceSpan("caller.sql", 1, 1),
                [new ProcCallArgument("@Status", new SqlType(SqlTypeCategory.NVarChar, Length: 20), false, null, true,
                    new ProcCallLiteralArgument("Active", "caller.sql", 1, 1, 1))]),
            new ProcCallEdge(null, "dbo.usp_Test", new SourceSpan("caller.sql", 2, 1),
                [new ProcCallArgument("@Status", new SqlType(SqlTypeCategory.NVarChar, Length: 20), false, null, true,
                    new ProcCallLiteralArgument("Inactive", "caller.sql", 2, 1, 1))]),
        ]);

        var result = DynamicSqlScannerV2.Scan(
            Parse("CREATE PROCEDURE dbo.usp_Test @Status NVARCHAR(20) AS BEGIN EXEC('SELECT ' + @Status); END;"),
            callGraph: callGraph);

        // Both known callers' literals, plus an unresolved alternative for any external caller
        // this scan's own call graph can't see - a stored procedure is a public surface.
        Assert.Equal(3, result.AnalyzableScripts.Count);
        var texts = result.AnalyzableScripts.Select(s => s.InnerText).ToList();
        Assert.Contains("SELECT Active", texts);
        Assert.Contains("SELECT Inactive", texts);
    }

    [Fact]
    public void FormalParameter_OneOfMultipleCallersNonLiteral_FallsBackToTypedPlaceholder()
    {
        var callGraph = new ProcCallGraph([
            new ProcCallEdge(null, "dbo.usp_Test", new SourceSpan("caller.sql", 1, 1),
                [new ProcCallArgument("@Status", new SqlType(SqlTypeCategory.NVarChar, Length: 20), false, null, true,
                    new ProcCallLiteralArgument("Active", "caller.sql", 1, 1, 1))]),
            new ProcCallEdge(null, "dbo.usp_Test", new SourceSpan("caller.sql", 2, 1),
                [new ProcCallArgument("@Status", new SqlType(SqlTypeCategory.NVarChar, Length: 20), false, "@SomeVariable", false, null)]),
        ]);

        var result = DynamicSqlScannerV2.Scan(
            Parse("CREATE PROCEDURE dbo.usp_Test @Status NVARCHAR(20) AS BEGIN EXEC('SELECT ' + @Status); END;"),
            callGraph: callGraph);

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    [Fact]
    public void OrdinaryCall_OutputArgument_SeededFromKnownCalleeSummary_InsteadOfTainted()
    {
        // Line 1 = DECLARE, line 2 = the EXEC ... OUTPUT call site, line 3 = the final EXEC(...) -
        // the call graph's own SourceSpan must match line 2 exactly (DynamicSqlCfg.CompileExecute
        // looks up ProcCallGraph.EdgeAt by this call site's own real position).
        var callSite = new SourceSpan(SourcePath, 2, 1);
        var callGraph = new ProcCallGraph([
            new ProcCallEdge(null, "dbo.usp_Helper", callSite,
                [new ProcCallArgument("@Out", new SqlType(SqlTypeCategory.NVarChar, Length: 50), FormalParameterIsOutput: true, CallerVariableName: "@rc", IsLiteral: false)]),
        ]);
        var outputSummaryIndex = new Dictionary<(string, string), IReadOnlyList<string>>
        {
            [("dbo.usp_Helper", "@Out")] = ["fixed-output"],
        };

        var result = DynamicSqlScannerV2.Scan(
            Parse("DECLARE @rc NVARCHAR(50);\nEXEC dbo.usp_Helper @Out = @rc OUTPUT;\nEXEC('SELECT ' + @rc);"),
            callGraph: callGraph, outputSummaryIndex: outputSummaryIndex);

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT fixed-output", script.InnerText);
        Assert.Equal(FindingConfidence.High, script.Confidence);
    }
}
