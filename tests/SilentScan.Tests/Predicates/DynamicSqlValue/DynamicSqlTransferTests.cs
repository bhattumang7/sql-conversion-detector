using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;

namespace SilentScan.Tests.Predicates.DynamicSqlValue;

/// <summary>
/// End-to-end slice: <see cref="DynamicSqlCfg"/> + <see cref="DynamicSqlTransfer"/> +
/// <see cref="ExpressionEvaluator"/> wired together over real parsed T-SQL, covering DECLARE,
/// SET (including <c>+=</c>), SELECT-assignment, and the havoc-default for an unmodeled
/// statement (docs/dynamic-sql-rebuild-plan.md Phase 3 §4). EXEC/script emission and parameter/
/// call-graph/output-summary seeding are a separate, later increment - not exercised here.
/// </summary>
public sealed class DynamicSqlTransferTests
{
    private const int Cap = 32;
    private const string SourcePath = "test.sql";

    private static Dictionary<string, SqlTextValue> Run(string sql) => Run(sql, out _, out _);

    private static Dictionary<string, SqlTextValue> Run(string sql, out List<DynamicSqlFinding> findings, out List<DynamicSqlScript> scripts)
    {
        var result = SqlScriptParser.ParseText(SourcePath, sql);
        Assert.False(result.HasErrors, string.Join(';', result.Errors.Select(e => e.Message)));
        var script = Assert.IsType<TSqlScript>(result.Fragment);
        var statements = script.Batches[0].Statements;

        findings = [];
        scripts = [];
        var context = new TransferContext(
            new Dictionary<string, SqlType>(StringComparer.OrdinalIgnoreCase), SourcePath, Cap,
            DynamicSqlScope.None, findings, scripts);
        var cfg = new DynamicSqlCfg(SourcePath, Cap, s => DynamicSqlTransfer.CompileLeaf(s, context));
        return cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));
    }

    private static string LitText(SqlTextValue value)
    {
        var template = Assert.IsType<SqlTextValue.Template>(value);
        var lit = Assert.IsType<TemplatePiece.Lit>(Assert.Single(template.Pieces));
        return lit.Text;
    }

    private static string FlattenLitText(SqlTextValue value)
    {
        var template = Assert.IsType<SqlTextValue.Template>(value);
        return string.Concat(template.Pieces.Select(p => Assert.IsType<TemplatePiece.Lit>(p).Text));
    }

    private static string TaintReason(SqlTextValue value) => Assert.IsType<SqlTextValue.Tainted>(value).Reason;

    [Fact]
    public void Declare_WithLiteralInitializer_Folds()
    {
        var result = Run("DECLARE @x NVARCHAR(50) = 'hello';");

        Assert.Equal("hello", LitText(result["@x"]));
        Assert.Equal(new SqlType(SqlTypeCategory.NVarChar, Length: 50), result["@x"].DeclaredType);
    }

    [Fact]
    public void Declare_NoInitializer_ProducesUninitializedDeclareHole()
    {
        var result = Run("DECLARE @x NVARCHAR(50);");

        var template = Assert.IsType<SqlTextValue.Template>(result["@x"]);
        var hole = Assert.IsType<TemplatePiece.Hole>(Assert.Single(template.Pieces));
        Assert.Equal(HoleKind.UninitializedDeclare, hole.Kind);
        Assert.Equal(new SqlType(SqlTypeCategory.NVarChar, Length: 50), hole.Type);
    }

    [Fact]
    public void Declare_ExplicitNullInitializer_TreatedSameAsNoInitializer()
    {
        var result = Run("DECLARE @x NVARCHAR(50) = NULL;");

        var template = Assert.IsType<SqlTextValue.Template>(result["@x"]);
        var hole = Assert.IsType<TemplatePiece.Hole>(Assert.Single(template.Pieces));
        Assert.Equal(HoleKind.UninitializedDeclare, hole.Kind);
    }

    [Fact]
    public void Set_SimpleAssignment_Folds()
    {
        var result = Run("DECLARE @x NVARCHAR(50); SET @x = 'value';");

        Assert.Equal("value", LitText(result["@x"]));
    }

    [Fact]
    public void Set_AddEquals_ConcatenatesOntoExistingValue()
    {
        var result = Run("DECLARE @x NVARCHAR(50) = 'a'; SET @x += 'b';");

        Assert.Equal("ab", FlattenLitText(result["@x"]));
    }

    [Fact]
    public void Set_AddEquals_OnUninitializedDeclareHole_AppendsRatherThanTainting()
    {
        // @x starts as an UninitializedDeclare hole (unknown content, known type) - += still
        // APPENDS onto it (Concat never requires a fully-concrete left operand), preserving more
        // information than tainting outright: "whatever @x's real value is, followed by 'b'".
        var result = Run("DECLARE @x NVARCHAR(50); SET @x += 'b';");

        var template = Assert.IsType<SqlTextValue.Template>(result["@x"]);
        Assert.Equal(2, template.Pieces.Count);
        Assert.Equal(HoleKind.UninitializedDeclare, Assert.IsType<TemplatePiece.Hole>(template.Pieces[0]).Kind);
        Assert.Equal("b", Assert.IsType<TemplatePiece.Lit>(template.Pieces[1]).Text);
    }

    [Fact]
    public void Set_UnsupportedAssignmentKind_Taints()
    {
        var result = Run("DECLARE @x INT = 5; SET @x -= 1;");

        Assert.Equal("unsupported-assignment", TaintReason(result["@x"]));
    }

    [Fact]
    public void SelectAssignment_PureShape_Folds()
    {
        var result = Run("DECLARE @x NVARCHAR(50); SELECT @x = 'literal';");

        Assert.Equal("literal", LitText(result["@x"]));
    }

    [Fact]
    public void SelectAssignment_WithFromClause_TaintsSelectAssignmentNotPure()
    {
        var result = Run("DECLARE @x NVARCHAR(50); SELECT @x = name FROM sys.tables;");

        Assert.Equal("select-assignment-not-pure", TaintReason(result["@x"]));
    }

    [Fact]
    public void FetchIntoVariable_UnmodeledStatement_HavocsToTypedHoleWhenDeclaredTypeKnown()
    {
        var result = Run(
            "DECLARE @x NVARCHAR(50) = 'before'; " +
            "DECLARE cur CURSOR FOR SELECT name FROM sys.tables; " +
            "FETCH NEXT FROM cur INTO @x;");

        var template = Assert.IsType<SqlTextValue.Template>(result["@x"]);
        var hole = Assert.IsType<TemplatePiece.Hole>(Assert.Single(template.Pieces));
        Assert.Equal(HoleKind.HavocWrite, hole.Kind);
        Assert.Equal(new SqlType(SqlTypeCategory.NVarChar, Length: 50), hole.Type);
    }

    [Fact]
    public void PrintStatement_WritesNoVariables_LeavesStateUntouched()
    {
        var result = Run("DECLARE @x NVARCHAR(50) = 'unchanged'; PRINT 'hello';");

        Assert.Equal("unchanged", LitText(result["@x"]));
    }

    [Fact]
    public void Exec_LiteralString_EmitsOneHighConfidenceScript()
    {
        Run("EXEC('SELECT * FROM Users');", out var findings, out var scripts);

        var script = Assert.Single(scripts);
        Assert.Equal("SELECT * FROM Users", script.InnerText);
        Assert.Equal(FindingConfidence.High, script.Confidence);
        Assert.Empty(findings);
    }

    [Fact]
    public void Exec_ConcatenatedLiteralAndVariable_FoldsIntoOneScript()
    {
        Run("DECLARE @tbl NVARCHAR(50) = 'Users'; EXEC('SELECT * FROM ' + @tbl);", out var findings, out var scripts);

        var script = Assert.Single(scripts);
        Assert.Equal("SELECT * FROM Users", script.InnerText);
        Assert.Empty(findings);
    }

    [Fact]
    public void Exec_UnresolvedVariable_EmitsUnanalyzableFinding()
    {
        Run("EXEC(@sql);", out var findings, out var scripts);

        Assert.Empty(scripts);
        var finding = Assert.Single(findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("variable-not-in-scope", finding.Reason);
    }

    [Fact]
    public void Exec_BranchDivergentAssembly_EmitsOneScriptPerAlternative()
    {
        Run(
            "DECLARE @sql NVARCHAR(MAX) = 'SELECT 1'; " +
            "IF 1 = 1 SET @sql = 'SELECT 2'; " +
            "EXEC(@sql);",
            out var findings, out var scripts);

        Assert.Empty(findings);
        var texts = scripts.Select(s => s.InnerText).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(["SELECT 1", "SELECT 2"], texts);
    }

    [Fact]
    public void Exec_HoleInAssembly_EmitsMediumConfidenceScriptWithPlaceholder()
    {
        Run("DECLARE @sql NVARCHAR(MAX); EXEC(@sql);", out var findings, out var scripts);

        Assert.Empty(findings);
        var script = Assert.Single(scripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
        Assert.NotNull(script.PlaceholderOccurrences);
        Assert.Single(script.PlaceholderOccurrences!);
    }

    [Fact]
    public void SpExecuteSql_PositionalStatementArgument_EmitsScript()
    {
        Run("EXEC sp_executesql N'SELECT * FROM Users';", out var findings, out var scripts);

        var script = Assert.Single(scripts);
        Assert.Equal("SELECT * FROM Users", script.InnerText);
        Assert.Empty(findings);
    }

    [Fact]
    public void SpExecuteSql_WithParameterDeclarationText_CapturesItVerbatim()
    {
        Run(
            "EXEC sp_executesql N'SELECT * FROM Users WHERE Id = @Id', N'@Id INT', @Id = 5;",
            out var findings, out var scripts);

        var script = Assert.Single(scripts);
        Assert.Equal("@Id INT", script.ParameterDeclarationText);
        Assert.Empty(findings);
    }

    [Fact]
    public void SpExecuteSql_NoArguments_EmitsUnanalyzableFinding()
    {
        Run("EXEC sp_executesql;", out var findings, out var scripts);

        Assert.Empty(scripts);
        Assert.Equal("non-literal-argument", Assert.Single(findings).Reason);
    }

    [Fact]
    public void OrdinaryProcedureCall_TaintsReferencedTrackedVariables()
    {
        var result = Run(
            "DECLARE @rc INT = 1; DECLARE @unrelated NVARCHAR(50) = 'kept'; " +
            "EXEC @rc = dbo.SomeProc @rc OUTPUT;",
            out var findings, out var scripts);

        Assert.Empty(scripts);
        Assert.Empty(findings); // the ordinary-call path taints state; it never emits a finding/script itself
        Assert.Equal("unsupported-execute-form", TaintReason(result["@rc"]));
        Assert.Equal("kept", LitText(result["@unrelated"])); // never mentioned by this call, so untouched
    }
}
