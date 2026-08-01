using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class DynamicSqlScannerTests
{
    private static DynamicSqlExtractionResult Scan(string sql)
    {
        var result = new SqlScriptParser().ParseText("test.sql", sql);
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
        Assert.Equal("non-literal-expression", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
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
}
