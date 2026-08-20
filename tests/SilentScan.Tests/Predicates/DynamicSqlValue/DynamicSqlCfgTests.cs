using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates.DynamicSqlValue;

/// <summary>
/// Exercises <see cref="DynamicSqlCfg"/>'s graph-building and fixpoint mechanics in isolation
/// from the real transfer functions (docs/dynamic-sql-rebuild-plan.md Phase 3 §4) - a
/// minimal test-only leaf compiler treats a <c>SET @x = 'literal'</c> as the ONLY meaningful
/// statement (assigning a one-piece <see cref="SqlTextValue.Template"/>) and records every
/// visited statement in order, so these tests can verify the graph shape (sequencing, IF/WHILE/
/// TRY-CATCH/GOTO wiring, join behavior, emission suppression during the fixpoint) without
/// depending on <see cref="BuiltinRegistry"/> or any expression evaluator.
/// </summary>
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

    /// <summary>Only understands <c>SET @name = 'literal'</c> - everything else is a no-op step (so IF/WHILE/TRY-CATCH bodies containing other statement kinds don't blow up the test, they just don't affect state).</summary>
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
        Assert.Equal(3, emitted.Count); // each SET emitted exactly once in the final pass
    }

    [Fact]
    public void EmissionIsSuppressedDuringFixpoint_AndRunsOnceInFinalPass()
    {
        var statements = ParseStatements("SET @x = 'a';");
        var emitted = new List<string>();
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, emitted));

        cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        // A straight-line scope has no loop needing more than one fixpoint round, but even so,
        // the log must show exactly ONE emission - proof that "emit" only ever fires in the
        // dedicated final pass, not during the (here, single) fixpoint round too.
        Assert.Equal(["SetVariableStatement"], emitted);
    }

    [Fact]
    public void IfWithoutElse_JoinsToOriginalValueOnFalseBranch()
    {
        var statements = ParseStatements("SET @x = 'a'; IF 1 = 1 BEGIN SET @x = 'b'; END SET @y = 'after';");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        // @x diverges (THEN sets 'b', implicit ELSE keeps 'a') - two distinct Templates joined
        // under the IF's own guard produce a single-piece Choice, not a collapsed value.
        var xTemplate = Assert.IsType<SqlTextValue.Template>(result["@x"]);
        var choice = Assert.IsType<TemplatePiece.Choice>(Assert.Single(xTemplate.Pieces));
        Assert.Equal(2, choice.Alternatives.Count);
        Assert.Equal("after", LitText(result["@y"])); // unconditional statement after the IF is untouched by the join
    }

    [Fact]
    public void IfStatement_VariableUntouchedByEitherBranch_MergesToItsOwnUnchangedValue()
    {
        var statements = ParseStatements("SET @x = 'unchanged'; IF 1 = 1 BEGIN SET @y = 'then-only'; END ELSE BEGIN SET @z = 'else-only'; END");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        // @x is the SAME value object on both incoming paths (neither branch touches it) -
        // Join's structural-equality short-circuit merges it to itself, never wrapping it in a
        // Choice of two identical alternatives.
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

        // Both IFs render the identical guard text ("@flag = 1") - the second join must MERGE
        // into the Choice the first join already produced, not nest a Choice-of-Choice one
        // level deeper (SqlTextValue.Join's own same-guard-alternatives merge).
        var xTemplate = Assert.IsType<SqlTextValue.Template>(result["@x"]);
        var choice = Assert.IsType<TemplatePiece.Choice>(Assert.Single(xTemplate.Pieces));
        Assert.True(choice.Alternatives.Count <= 3); // base/first, or with second's own re-join - never a deeper nested Choice
        Assert.All(choice.Alternatives, alt => Assert.IsType<TemplatePiece.Lit>(Assert.Single(alt.Pieces)));
    }

    [Fact]
    public void WhileLoop_FixpointConverges_WithoutHittingMaxRounds()
    {
        var statements = ParseStatements("SET @x = 'start'; WHILE @i < 10 BEGIN SET @x = 'looped'; SET @i = @i + 1; END SET @y = 'after';");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        // @x is either 'start' (loop never entered) or 'looped' (loop ran ≥1 time) - a Choice
        // between exactly those two, converged and stable, not an ever-growing chain.
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

        // @x is either 'in-try' (TRY completed) or 'before' (CATCH ran, TRY's own SET never
        // committed - CATCH's edge originates from the PRE-TRY block, never from mid-TRY).
        var xTemplate = Assert.IsType<SqlTextValue.Template>(result["@x"]);
        var choice = Assert.IsType<TemplatePiece.Choice>(Assert.Single(xTemplate.Pieces));
        var texts = choice.Alternatives.Select(LitText).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(["before", "in-try"], texts);
    }

    [Fact]
    public void TryCatch_VariableDeclaredOnlyInTry_IsVisibleInCatchAsTypedHole()
    {
        // T-SQL locals are batch/proc scoped, not block-scoped - @errorContext, declared only
        // inside TRY, is still legal to reference from CATCH (the classic "log the dynamic SQL
        // that just failed" pattern). Without the TryOnlyDeclaration seeding, this reference would
        // fail lookup entirely rather than resolve to a typed placeholder.
        // @errorContext is declared but never assigned inside TRY - TRY's own exit state never
        // touches the key at all, so the join at TRY/CATCH's end takes CATCH's seeded Hole
        // unmerged (DynamicSqlCfg.MergeStateInto: a key present on only one path passes straight
        // through), keeping this test focused purely on the seeding step under test.
        var statements = ParseStatements(
            "BEGIN TRY " +
            "DECLARE @errorContext NVARCHAR(200); " +
            "END TRY " +
            "BEGIN CATCH " +
            "SET @y = @errorContext; " +
            "END CATCH");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        // Only @errorContext's DECLAREd type survives into CATCH, as a Hole - not a
        // "variable-not-in-scope" failure.
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

        Assert.Equal("start", LitText(result["@x"])); // never overwritten - the GOTO skipped that statement entirely
        Assert.Equal("reached", LitText(result["@y"]));
    }

    [Fact]
    public void ReturnStatement_ContributesItsOwnStateToTheFinalMergedState()
    {
        // A RETURN's own state is one of the ways the scope can genuinely stop running (a real
        // OUTPUT parameter set right before RETURN takes effect on the caller) - it is excluded
        // from the ordinary fallthrough graph (nothing is reachable FROM a RETURN block), but its
        // OWN state still contributes to the scope's overall final result, alongside the natural
        // end-of-body fallthrough. Both are genuinely possible, so both survive as a Choice.
        var statements = ParseStatements("SET @x = 'a'; IF 1 = 1 BEGIN SET @x = 'returned'; RETURN; END SET @x = 'fallthrough';");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        var xTemplate = Assert.IsType<SqlTextValue.Template>(result["@x"]);
        var choice = Assert.IsType<TemplatePiece.Choice>(Assert.Single(xTemplate.Pieces));
        var texts = choice.Alternatives.Select(LitText).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(["fallthrough", "returned"], texts);
    }
}
