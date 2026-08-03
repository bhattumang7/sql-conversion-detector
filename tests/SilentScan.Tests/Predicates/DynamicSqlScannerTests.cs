using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class DynamicSqlScannerTests
{
    private static DynamicSqlExtractionResult Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return DynamicSqlScanner.Scan(result);
    }

    [Fact]
    public void Scan_ExecOfLocallyDeclaredLiteralVariable_TierC_ProducesAnalyzableScript()
    {
        // Tier C: a straight-line DECLARE-with-literal-initializer immediately reaching the
        // EXEC is provably constant - CLAUDE.md's dynamic SQL policy explicitly wants this
        // traced, not lumped into the unanalyzable bucket just because it's a variable.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfUndeclaredVariable_Unanalyzable()
    {
        // No DECLARE/proc-parameter for @sql anywhere in scope - genuinely unknowable.
        var result = Scan("EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("undeclared-variable", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedFromFunctionCall_Unanalyzable()
    {
        // The assignment itself isn't a bare literal/concatenation - CLAUDE.md's known hard
        // case (function calls like QUOTENAME aren't reimplemented, just declined honestly).
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = UPPER(N'select 1'); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:function-call", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedFromColumnReference_ReasonNamesColumnReference()
    {
        // ScriptDom parses an unqualified identifier here as a ColumnReferenceExpression
        // regardless of whether a real FROM scope exists for it (that's a semantic question this
        // syntax-level scanner never answers) - exactly the shape TryFoldExpression's own
        // column-reference case exists to name.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'SELECT 1' + SomeColumn; EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:column-reference", finding.Reason);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedFromScalarSubquery_ReasonNamesSubquery()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = (SELECT TOP 1 SomeColumn FROM dbo.T); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:subquery", finding.Reason);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedFromCaseExpression_ReasonNamesConditional()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = CASE WHEN 1 = 1 THEN N'SELECT 1' ELSE N'SELECT 2' END; EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:conditional", finding.Reason);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedFromCastExpression_ReasonNamesCastOrConvert()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = CAST(N'SELECT 1' AS NVARCHAR(MAX)); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:cast-or-convert", finding.Reason);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedFromSubtraction_ReasonNamesUnsupportedOperator()
    {
        // Add is the only BinaryExpressionType folded (string concatenation) - every other
        // operator on a dynamic SQL text expression is its own, distinct, rarer shape.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'SELECT 1' + (5 - 1); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:unsupported-operator", finding.Reason);
    }

    [Fact]
    public void Scan_ExecOfVariableReassignedInsideIfBranch_Unanalyzable()
    {
        // A reassignment under a branch makes the value entering the EXEC ambiguous -
        // CLAUDE.md: "no assignment under IF/WHILE/TRY-CATCH/GOTO-reachable branches".
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "IF 1 = 1 BEGIN SET @sql = N'SELECT 2'; END " +
            "EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("diverges-across-if-branches", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfVariableUntouchedByUnrelatedIfBranch_TierC_ProducesAnalyzableScript()
    {
        // The branch exists but never touches @sql - folding must survive it (precise, not a
        // blanket "any branch anywhere taints everything" over-approximation).
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "DECLARE @other INT = 0; " +
            "IF 1 = 1 BEGIN SET @other = 1; END " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableInProcContainingGoto_Unanalyzable()
    {
        // A GOTO anywhere in the proc can jump past/around assignments in ways a straight-line
        // walk can't safely reason about - folding is disabled for the whole scope.
        var result = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "GOTO Skip; " +
            "Skip: " +
            "EXEC(@sql); " +
            "END;");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("goto-or-label-in-scope", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfAccumulatedConcatenation_TierC_ProducesAnalyzableScript()
    {
        // The common accumulation pattern: SET @sql = @sql + '...' (and += equivalent).
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 '; " +
            "SET @sql = @sql + N'WHERE 1 = 1'; " +
            "SET @sql += N' AND 2 = 2'; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1 WHERE 1 = 1 AND 2 = 2", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableAfterWhileLoopThatTouchesIt_Unanalyzable()
    {
        // A while body may run zero, one, or many times, so nothing it reassigns can be
        // trusted after the loop.
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "DECLARE @i INT = 0; " +
            "WHILE @i < 1 BEGIN SET @sql = N'SELECT 2'; SET @i += 1; END " +
            "EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("while-loop-body", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecInsideWhileLoopUsingPreLoopValue_TierC_ProducesAnalyzableScript()
    {
        // An EXEC *inside* the loop body can still fold using state as of loop entry plus
        // whatever the body itself assigned before reaching it - valid on the first (and every
        // identical) iteration.
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "DECLARE @i INT = 0; " +
            "WHILE @i < 1 BEGIN EXEC(@sql); SET @i += 1; END");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableAfterTryCatchThatTouchesIt_Unanalyzable()
    {
        // How far the TRY block got before an error is unknowable, so anything it (or CATCH)
        // reassigns is ambiguous afterward.
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "BEGIN TRY SET @sql = N'SELECT 2'; END TRY " +
            "BEGIN CATCH SET @sql = N'SELECT 3'; END CATCH " +
            "EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("diverges-across-try-catch", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfVariableUntouchedByUnrelatedTryCatch_TierC_ProducesAnalyzableScript()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "DECLARE @other INT = 0; " +
            "BEGIN TRY SET @other = 1; END TRY " +
            "BEGIN CATCH SET @other = 2; END CATCH " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableAfterOrdinaryPlainSelect_TierC_ProducesAnalyzableScript()
    {
        // An ordinary SELECT with no variable assignment can't mutate a local variable - must
        // not needlessly taint anything.
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "SELECT 1; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableAfterUnrecognizedStatementKind_Unanalyzable()
    {
        // Precision-first default: a statement kind this scanner doesn't explicitly model
        // (here, INSERT) taints everything tracked rather than risk folding through a value
        // some unmodeled mechanism (e.g. OUTPUT INTO) might have silently changed.
        var result = Scan(
            "CREATE TABLE dbo.T (Col INT); " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "INSERT INTO dbo.T (Col) VALUES (1); " +
            "EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("unsupported-statement-in-scope", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfPureSelectAssignment_TierC_ProducesAnalyzableScript()
    {
        // SELECT @x = expr [, @y = expr2] with no FROM clause is a pure variable assignment,
        // just like SET - the other common way T-SQL builds up dynamic SQL text.
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 '; " +
            "SELECT @sql = @sql + N'WHERE 1 = 1';" +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1 WHERE 1 = 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfMultiAssignmentSelect_TierC_ProducesAnalyzableScript()
    {
        var result = Scan(
            "DECLARE @a NVARCHAR(20); DECLARE @b NVARCHAR(20); " +
            "SELECT @a = N'SELECT ', @b = N'1'; " +
            "EXEC(@a + @b);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfSelectAssignmentWithFromClause_Unanalyzable()
    {
        // SELECT @x = Col FROM T is data/row-order dependent - genuinely unknowable, not the
        // pure-assignment shape.
        var result = Scan(
            "CREATE TABLE dbo.T (Col NVARCHAR(50)); " +
            "DECLARE @sql NVARCHAR(MAX); " +
            "SELECT @sql = Col FROM dbo.T; " +
            "EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("select-assignment-not-pure", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfSelectAssignmentMixedWithRealColumn_Unanalyzable()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "SELECT @sql = N'SELECT 2', 1 AS RealColumn; " +
            "EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("select-assignment-not-pure", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfVariableWithNoInitializer_Unanalyzable()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("no-initializer", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfVariableReassignedInsideElseBranch_Unanalyzable()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "IF 1 = 0 BEGIN SET @sql = N'SELECT 2'; END " +
            "ELSE BEGIN SET @sql = N'SELECT 3'; END " +
            "EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("diverges-across-if-branches", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfConcatenationWhereLeftOperandUndeclared_Unanalyzable()
    {
        // The left side of the SET's own `+` fails to fold - a different code path than the
        // right side failing.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = @undeclared + N'x'; EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("undeclared-variable", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfConcatenationWhereRightOperandUndeclared_Unanalyzable()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'x' + @undeclared; EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("undeclared-variable", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_MultiStatementFunctionBody_TierC_ProducesAnalyzableScript()
    {
        // CreateFunctionStatement bodies (multi-statement TVFs, scalar functions) get their
        // own fresh variable scope, same as CREATE PROCEDURE.
        var result = Scan(
            "CREATE FUNCTION dbo.udf_Test() RETURNS INT AS " +
            "BEGIN " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "EXEC(@sql); " +
            "RETURN 1; " +
            "END;");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecInAlterProcedureBody_TierC_ProducesAnalyzableScript()
    {
        // Regression: real-world corpus code (e.g. Brent Ozar's First Responder Kit) commonly
        // uses "stub CREATE PROCEDURE ... AS RETURN 0" followed by ALTER PROCEDURE for the
        // real body - matching only CreateProcedureStatement would silently never walk into
        // the ALTER'd body at all (not even reporting it Unanalyzable - just never visited).
        var result = Scan(
            "ALTER PROCEDURE dbo.usp_Test AS BEGIN " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "EXEC(@sql); " +
            "END;");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecInCreateOrAlterProcedureBody_TierC_ProducesAnalyzableScript()
    {
        var result = Scan(
            "CREATE OR ALTER PROCEDURE dbo.usp_Test AS BEGIN " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "EXEC(@sql); " +
            "END;");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecInAlterFunctionBody_TierC_ProducesAnalyzableScript()
    {
        var result = Scan(
            "ALTER FUNCTION dbo.udf_Test() RETURNS INT AS BEGIN " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "EXEC(@sql); " +
            "RETURN 1; " +
            "END;");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecInCreateTriggerBody_TierC_ProducesAnalyzableScript()
    {
        var result = Scan(
            "CREATE TABLE dbo.T (Col INT);\n" +
            "GO\n" +
            "CREATE TRIGGER dbo.trg_Test ON dbo.T AFTER INSERT AS BEGIN " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "EXEC(@sql); " +
            "END;");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecInAlterTriggerBody_TierC_ProducesAnalyzableScript()
    {
        var result = Scan(
            "CREATE TABLE dbo.T (Col INT);\n" +
            "GO\n" +
            "ALTER TRIGGER dbo.trg_Test ON dbo.T AFTER INSERT AS BEGIN " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "EXEC(@sql); " +
            "END;");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExternalClrProcedure_NoStatementListBody_DoesNotThrow()
    {
        // A body-less proc declaration (e.g. a CLR proc's EXTERNAL NAME body) has no
        // StatementList - real-world corpus code (First Responder Kit) hits this.
        var result = Scan("CREATE PROCEDURE dbo.usp_Test AS EXTERNAL NAME Assembly.Class.Method;");

        Assert.Empty(result.Findings);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_InlineTableValuedFunction_NoStatementListBody_DoesNotThrow()
    {
        // An inline TVF has no StatementList (its body is a single RETURN expression) - must
        // be handled without throwing, even though it can't contain dynamic SQL.
        var result = Scan("CREATE FUNCTION dbo.udf_Test() RETURNS TABLE AS RETURN (SELECT 1 AS X);");

        Assert.Empty(result.Findings);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfParenthesizedLiteral_TierC_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = (N'SELECT 1'); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_SpExecuteSqlWithNoArguments_Unanalyzable()
    {
        var result = Scan("EXEC sp_executesql;");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-argument", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedWithSubtractEquals_Unanalyzable()
    {
        // Only Equals/AddEquals are meaningful for string values; other compound operators are
        // declined rather than guessed at.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; SET @sql -= N'x'; EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("unsupported-assignment", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfStringLiteral_ProducesAnalyzableScript()
    {
        var result = Scan("EXEC('SELECT 1');");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfConcatenatedLiterals_ProducesAnalyzableScriptWithFoldedText()
    {
        var result = Scan("EXEC('SELECT ' + '1');");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfLiteralConcatenatedWithLocallyDeclaredVariable_TierC_ProducesAnalyzableScript()
    {
        // Tier C: a bare literal concatenated with a locally-folded variable is provably
        // constant end to end.
        var result = Scan("DECLARE @x NVARCHAR(10) = N'x'; EXEC('SELECT ' + @x);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT x", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfConcatenationWithUndeclaredVariable_Unanalyzable()
    {
        var result = Scan("EXEC('SELECT ' + @x);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("undeclared-variable", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_SpExecuteSqlWithLocallyDeclaredLiteralVariable_TierC_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; EXEC sp_executesql @sql;");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_SpExecuteSqlWithUndeclaredVariable_Unanalyzable()
    {
        var result = Scan("EXEC sp_executesql @sql;");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("undeclared-variable", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_SpExecuteSqlWithLiteral_ProducesAnalyzableScript()
    {
        var result = Scan("EXEC sp_executesql N'SELECT 1';");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_NoExecuteStatements_NoFindings()
    {
        var result = Scan("SELECT 1;");

        Assert.Empty(result.Findings);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_RegularProcedureExec_NoFinding()
    {
        // EXEC dbo.usp_DoThing is a normal proc call, not dynamic SQL - must not fire.
        var result = Scan("EXEC dbo.usp_DoThing;");

        Assert.Empty(result.Findings);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfVariableMutatedByPriorProcCallWithOutput_Unanalyzable()
    {
        // The P0 fix: `EXEC dbo.BuildQuery @sql OUTPUT` can mutate @sql through a mechanism
        // this scanner has no visibility into. Before this fix, an unrecognized ExecuteEntity
        // (any ordinary procedure call) fell through HandleExecute doing nothing at all, so the
        // later EXEC(@sql) folded the STALE pre-call literal and reported AnalyzedLiteral for
        // SQL that never actually ran.
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "EXEC dbo.BuildQuery @sql OUTPUT; " +
            "EXEC(@sql);");

        Assert.Empty(result.AnalyzableScripts);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("unsupported-execute-form", finding.Reason); // the second EXEC(@sql) site
    }

    [Fact]
    public void Scan_ExecOfVariableMutatedByProcCallWithReturnAssignment_Unanalyzable()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "DECLARE @rc INT; " +
            "EXEC @rc = dbo.BuildQuery @sql; " +
            "EXEC(@sql);");

        Assert.Empty(result.AnalyzableScripts);
        Assert.Single(result.Findings);
    }

    [Fact]
    public void Scan_ExecInsideWhileLoopThatSelfMutatesTheExecutedVariable_Unanalyzable()
    {
        // The counterpart to Scan_ExecInsideWhileLoopUsingPreLoopValue_TierC above: here the
        // loop body itself reassigns @sql AFTER the EXEC reads it, in program order. Folding
        // this against loop-entry state is only valid for iteration 1 - iteration 2+ runs SQL
        // this scanner never analyzed, while the site would otherwise still report
        // AnalyzedLiteral. This is exactly the DynamicSqlScanner audit's "iteration 2+ executes
        // different SQL under an AnalyzedLiteral claim" finding.
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "DECLARE @i INT = 0; " +
            "WHILE @i < 3 BEGIN EXEC(@sql); SET @sql += N' AND 1=1'; SET @i += 1; END");

        Assert.Empty(result.AnalyzableScripts);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("while-loop-body-self-mutates", finding.Reason);
    }

    [Fact]
    public void Scan_ExecOfSelectAssignmentWithWhereClause_Unanalyzable()
    {
        // `SELECT @x = ... WHERE <cond>` assigns zero or one time depending on the WHERE -
        // unlike a FROM-less unconditional assignment, this is not certain to run at all, so
        // it must taint rather than fold as if it always executes.
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX); " +
            "DECLARE @flag BIT = 1; " +
            "SELECT @sql = N'SELECT 1' WHERE @flag = 1; " +
            "EXEC(@sql);");

        Assert.Empty(result.AnalyzableScripts);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("select-assignment-not-pure", finding.Reason);
    }

    // ------------------------------------------------------------------
    // Cross-call-edge seeding (roadmap "trace provably-constant dynamic SQL across proc-call
    // edges") - a proc's OWN parameter, folded into dynamic SQL built inside its OWN body, using
    // a literal this scan saw a CALLER pass at a call site the ProcCallGraph recorded. The graph
    // is hand-built here rather than via ProcCallGraphBuilder (which needs a real
    // DatabaseCatalog) - these tests exercise DynamicSqlScanner's own seeding logic in
    // isolation; ScanReportBuilder wiring the two together end-to-end belongs in a pipeline test.
    // ------------------------------------------------------------------

    private const string CalleeProcName = "dbo.usp_RunLookup";

    private static ProcCallGraph SingleCallerGraph(ProcCallArgument argument) =>
        new([new ProcCallEdge(null, CalleeProcName, new SourceSpan("caller.sql", 10, 5), [argument])]);

    private static DynamicSqlExtractionResult ScanWithCallGraph(string sql, ProcCallGraph callGraph)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return DynamicSqlScanner.Scan(result, callGraph: callGraph);
    }

    [Fact]
    public void Scan_ProcParamSeededFromSingleCallerLiteral_ProducesAnalyzableScript()
    {
        var literal = new ProcCallLiteralArgument("Active", "caller.sql", 10, 30, PrefixLength: 2);
        var graph = SingleCallerGraph(new ProcCallArgument("@Status", FormalParameterType: null, FormalParameterIsOutput: false, CallerVariableName: null, IsLiteral: true, literal));

        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status NVARCHAR(20) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            graph);

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1 WHERE Status = 'Active'", script.InnerText);
    }

    [Fact]
    public void Scan_ProcParamWithMultipleCallers_UnanalyzableWithSpecificReason()
    {
        // Two call sites - even if both happened to pass the same literal, this scan has no
        // general way to prove that without comparing every caller's value, so it must not
        // silently pick one and call it constant.
        var literal = new ProcCallLiteralArgument("Active", "caller.sql", 10, 30, PrefixLength: 2);
        var argument = new ProcCallArgument("@Status", null, false, null, true, literal);
        var graph = new ProcCallGraph([
            new ProcCallEdge(null, CalleeProcName, new SourceSpan("caller.sql", 10, 5), [argument]),
            new ProcCallEdge(null, CalleeProcName, new SourceSpan("caller.sql", 20, 5), [argument]),
        ]);

        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status NVARCHAR(20) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            graph);

        Assert.Empty(result.AnalyzableScripts);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("parameter-not-seeded:multiple-call-sites", finding.Reason);
    }

    [Fact]
    public void Scan_ProcParamWithSingleNonLiteralCaller_UnanalyzableWithSpecificReason()
    {
        // One call site, but the actual argument was a variable, not a literal - nothing this
        // scan can trace back to a concrete value.
        var argument = new ProcCallArgument("@Status", null, false, "@callerVar", IsLiteral: false, LiteralArgument: null);
        var graph = SingleCallerGraph(argument);

        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status NVARCHAR(20) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            graph);

        Assert.Empty(result.AnalyzableScripts);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("parameter-not-seeded:non-literal-caller", finding.Reason);
    }

    [Fact]
    public void Scan_ProcParamWithNoKnownCallers_FallsBackToUndeclaredVariable()
    {
        // Zero edges for this callee - unchanged from before cross-call-edge seeding existed;
        // a caller-blind scan reports the same generic reason it always has.
        var graph = new ProcCallGraph([]);

        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status NVARCHAR(20) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            graph);

        Assert.Empty(result.AnalyzableScripts);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("undeclared-variable", finding.Reason);
    }

    [Fact]
    public void Scan_OutputParamNeverSeededEvenWithSingleLiteralLookingCaller()
    {
        // FormalParameterIsOutput true means the argument flows callee-to-caller, never the
        // other direction - seeding it from anything would be backwards, so it must stay
        // unseeded (falls back to "undeclared-variable" exactly like a genuinely unknown one).
        var literal = new ProcCallLiteralArgument("Active", "caller.sql", 10, 30, PrefixLength: 2);
        var argument = new ProcCallArgument("@Status", null, FormalParameterIsOutput: true, null, true, literal);
        var graph = SingleCallerGraph(argument);

        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status NVARCHAR(20) OUTPUT AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            graph);

        Assert.Empty(result.AnalyzableScripts);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("undeclared-variable", finding.Reason);
    }
}
