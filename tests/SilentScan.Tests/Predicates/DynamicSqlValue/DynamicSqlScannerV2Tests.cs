using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates.DynamicSqlValue;

public sealed class DynamicSqlScannerV2Tests
{
    private const string SourcePath = "test.sql";

    private static SqlParseResult Parse(string sql)
    {
        var result = SqlScriptParser.ParseText(SourcePath, sql);
        Assert.False(result.HasErrors, string.Join(';', result.Errors.Select(e => e.Message)));
        return result;
    }

    private static DynamicSqlExtractionResult ScanWithCatalog(string ddl, string sql)
    {
        var ddlResult = SqlScriptParser.ParseText("ddl.sql", ddl);
        Assert.False(ddlResult.HasErrors, string.Join(';', ddlResult.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([ddlResult]);
        return DynamicSqlScannerV2.Scan(Parse(sql), catalog: catalog);
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

    [Fact]
    public void OrdinaryCall_CallGraphPresentButNoEdgeAtThisCallSite_FallsBackToHavocRatherThanSeedingFromSummary()
    {
        var callGraph = new ProcCallGraph([
            new ProcCallEdge(null, "dbo.usp_Helper", new SourceSpan(SourcePath, 99, 1),
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
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
        Assert.NotNull(script.PlaceholderOccurrences);
        Assert.DoesNotContain("fixed-output", script.InnerText);
    }

    [Fact]
    public void OrdinaryCall_KnownCalleeSummaryHasMultipleValues_JoinsThemIntoSeparateAnalyzableScripts()
    {
        var callSite = new SourceSpan(SourcePath, 2, 1);
        var callGraph = new ProcCallGraph([
            new ProcCallEdge(null, "dbo.usp_Helper", callSite,
                [new ProcCallArgument("@Out", new SqlType(SqlTypeCategory.NVarChar, Length: 50), FormalParameterIsOutput: true, CallerVariableName: "@rc", IsLiteral: false)]),
        ]);
        var outputSummaryIndex = new Dictionary<(string, string), IReadOnlyList<string>>
        {
            [("dbo.usp_Helper", "@Out")] = ["fixed-output-a", "fixed-output-b"],
        };

        var result = DynamicSqlScannerV2.Scan(
            Parse("DECLARE @rc NVARCHAR(50);\nEXEC dbo.usp_Helper @Out = @rc OUTPUT;\nEXEC('SELECT ' + @rc);"),
            callGraph: callGraph, outputSummaryIndex: outputSummaryIndex);

        Assert.Empty(result.Findings);
        var texts = result.AnalyzableScripts.Select(s => s.InnerText).ToList();
        Assert.Equal(2, texts.Count);
        Assert.Contains("SELECT fixed-output-a", texts);
        Assert.Contains("SELECT fixed-output-b", texts);
    }

    [Fact]
    public void SelectAssignmentFromSingleKnownTable_ParenthesizedVariablePlusColumnExpression_FoldsBothOperands()
    {
        var result = ScanWithCatalog(
            "CREATE TABLE dbo.Templates (Name VARCHAR(50) NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_Scratch AS
            BEGIN
                DECLARE @prefix VARCHAR(20) = 'PFX_';
                DECLARE @x VARCHAR(100);
                SELECT @x = (@prefix + Name) FROM dbo.Templates WHERE Name = 'Report';
                EXEC (@x);
            END
            """);

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
        Assert.StartsWith("PFX_", script.InnerText, StringComparison.Ordinal);
        Assert.NotNull(script.PlaceholderOccurrences);
        Assert.Single(script.PlaceholderOccurrences!);
    }
}
