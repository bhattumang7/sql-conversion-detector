using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates.DynamicSqlValue;

public sealed class DynamicSqlCfgTests
{
    private const int Cap = 32;
    private static readonly SqlType NVarCharMax = new(SqlTypeCategory.NVarChar, IsMax: true);

    private static IList<TSqlStatement> ParseStatements(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join(';', result.Errors.Select(e => e.Message)));
        var script = Assert.IsType<TSqlScript>(result.Fragment);
        return script.Batches[0].Statements;
    }

private static Action<Dictionary<string, SqlTextValue>, bool> CompileLeaf(TSqlStatement statement, List<string> emittedLog) => (state, emit) =>
    {
        if (emit)
        {
            emittedLog.Add(statement.GetType().Name);
        }

        if (statement is SetVariableStatement { Variable.Name: var name, Expression: StringLiteral literal })
        {
            state[name] = new SqlTextValue.Template([new TemplatePiece.Lit(literal.Value, new SourceSpan("test.sql", literal.StartLine, literal.StartColumn), PrefixLength: 1)]) { DeclaredType = NVarCharMax };
        }
    };

    private static string LitText(SqlTextValue value)
    {
        var template = Assert.IsType<SqlTextValue.Template>(value);
        var lit = Assert.IsType<TemplatePiece.Lit>(Assert.Single(template.Pieces));
        return lit.Text;
    }

    [Fact]
    public void StraightLineStatements_RunInOrder()
    {
        var statements = ParseStatements("SET @x = 'a'; SET @x = 'b'; SET @x = 'c';");
        var emitted = new List<string>();
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, emitted));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("c", LitText(result["@x"]));
        Assert.Equal(3, emitted.Count);    }

    [Fact]
    public void EmissionIsSuppressedDuringFixpoint_AndRunsOnceInFinalPass()
    {
        var statements = ParseStatements("SET @x = 'a';");
        var emitted = new List<string>();
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, emitted));

        cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(["SetVariableStatement"], emitted);
    }

    [Fact]
    public void IfWithoutElse_JoinsToOriginalValueOnFalseBranch()
    {
        var statements = ParseStatements("SET @x = 'a'; IF 1 = 1 BEGIN SET @x = 'b'; END SET @y = 'after';");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        var xTemplate = Assert.IsType<SqlTextValue.Template>(result["@x"]);
        var choice = Assert.IsType<TemplatePiece.Choice>(Assert.Single(xTemplate.Pieces));
        Assert.Equal(2, choice.Alternatives.Count);
        Assert.Equal("after", LitText(result["@y"]));    }

    [Fact]
    public void IfStatement_VariableUntouchedByEitherBranch_MergesToItsOwnUnchangedValue()
    {
        var statements = ParseStatements("SET @x = 'unchanged'; IF 1 = 1 BEGIN SET @y = 'then-only'; END ELSE BEGIN SET @z = 'else-only'; END");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("unchanged", LitText(result["@x"]));
    }

    [Fact]
    public void SameGuardTestedTwice_MergesAlternativesInsteadOfNesting()
    {
        var statements = ParseStatements(
            "SET @x = 'base'; " +
            "IF @flag = 1 SET @x = 'first'; " +
            "IF @flag = 1 SET @x = 'second';");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        var xTemplate = Assert.IsType<SqlTextValue.Template>(result["@x"]);
        var choice = Assert.IsType<TemplatePiece.Choice>(Assert.Single(xTemplate.Pieces));
        Assert.True(choice.Alternatives.Count <= 3);        Assert.All(choice.Alternatives, alt => Assert.IsType<TemplatePiece.Lit>(Assert.Single(alt.Pieces)));
    }

    [Fact]
    public void WhileLoop_FixpointConverges_WithoutHittingMaxRounds()
    {
        var statements = ParseStatements("SET @x = 'start'; WHILE @i < 10 BEGIN SET @x = 'looped'; SET @i = @i + 1; END SET @y = 'after';");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        var xTemplate = Assert.IsType<SqlTextValue.Template>(result["@x"]);
        var choice = Assert.IsType<TemplatePiece.Choice>(Assert.Single(xTemplate.Pieces));
        Assert.Equal(2, choice.Alternatives.Count);
        Assert.Equal("after", LitText(result["@y"]));
    }

    [Fact]
    public void TryCatch_CatchStartsFromPreTryState_NotFromInsideTry()
    {
        var statements = ParseStatements(
            "SET @x = 'before'; " +
            "BEGIN TRY SET @x = 'in-try'; END TRY " +
            "BEGIN CATCH SET @y = 'in-catch'; END CATCH");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        var xTemplate = Assert.IsType<SqlTextValue.Template>(result["@x"]);
        var choice = Assert.IsType<TemplatePiece.Choice>(Assert.Single(xTemplate.Pieces));
        var texts = choice.Alternatives.Select(LitText).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(["before", "in-try"], texts);
    }

    [Fact]
    public void TryCatch_VariableDeclaredOnlyInTry_IsVisibleInCatchAsTypedHole()
    {
        var statements = ParseStatements(
            "BEGIN TRY " +
            "DECLARE @errorContext NVARCHAR(200); " +
            "END TRY " +
            "BEGIN CATCH " +
            "SET @y = @errorContext; " +
            "END CATCH");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        Assert.True(result.ContainsKey("@errorContext"));
        var template = Assert.IsType<SqlTextValue.Template>(result["@errorContext"]);
        var hole = Assert.IsType<TemplatePiece.Hole>(Assert.Single(template.Pieces));
        Assert.Equal(HoleKind.TryOnlyDeclaration, hole.Kind);
    }

    [Fact]
    public void Goto_SkipsInterveningStatement()
    {
        var statements = ParseStatements(
            "SET @x = 'start'; " +
            "GOTO skip; " +
            "SET @x = 'skipped-over'; " +
            "skip: " +
            "SET @y = 'reached';");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("start", LitText(result["@x"]));        Assert.Equal("reached", LitText(result["@y"]));
    }

    [Fact]
    public void ReturnStatement_ContributesItsOwnStateToTheFinalMergedState()
    {
        var statements = ParseStatements("SET @x = 'a'; IF 1 = 1 BEGIN SET @x = 'returned'; RETURN; END SET @x = 'fallthrough';");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        var xTemplate = Assert.IsType<SqlTextValue.Template>(result["@x"]);
        var choice = Assert.IsType<TemplatePiece.Choice>(Assert.Single(xTemplate.Pieces));
        var texts = choice.Alternatives.Select(LitText).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(["fallthrough", "returned"], texts);
    }
}
