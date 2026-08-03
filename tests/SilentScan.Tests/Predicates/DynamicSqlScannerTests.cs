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
        // case (an ordinary function call not on the whitelisted string-builder list is not
        // reimplemented, just declined honestly). REVERSE is deliberately NOT one of the
        // whitelisted builders (DynamicSqlScanner.WhitelistedStringBuilders), unlike UPPER/LOWER/
        // LTRIM/RTRIM/LEFT/RIGHT/SUBSTRING/QUOTENAME which now fold - see
        // Scan_ExecOfVariableAssignedFromUpperOnAsciiLiteral_TierC_ProducesAnalyzableScript below
        // for that behavior.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = REVERSE(N'select 1'); EXEC(@sql);");

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

    // CASE/IIF and CAST/CONVERT are no longer unconditional declines - see the "CASE/IIF folding"
    // and "CAST/CONVERT folding" sections below for what each now folds and what still declines
    // (and why: CASE with both branches literal now unions; CAST to a pinned VARCHAR(n)/
    // NVARCHAR(n) target now truncates - only a non-string or CHAR/NCHAR target still declines,
    // under "non-literal-expression:cast-target-not-pinned", not the old generic
    // "non-literal-expression:cast-or-convert", which no longer exists as a reason at all).

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
    public void Scan_SetCursorVariable_TaintsRatherThanCrashes()
    {
        // SetVariableStatement.Expression is null when the RHS is modeled in a sibling
        // property instead - SET @c = CURSOR FOR ... puts the RHS in CursorDefinition. Must
        // taint @c as unsupported-assignment rather than NRE inside TryFoldExpression.
        var result = Scan(
            "DECLARE @c CURSOR; DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "SET @c = CURSOR FOR SELECT 1 AS x; SET @sql = N'SELECT 1'; EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableReassignedInsideIfBranch_BothBranchAssembliesAnalyzed()
    {
        // Branch-fold coverage (roadmap "trace dynamic SQL across IF/ELSE branches"): an IF's
        // THEN/ELSE are mutually exclusive, fully-determined outcomes, so when BOTH fold to a
        // constant value (here: reassigned, or left unchanged - the implicit ELSE), the real
        // value after the statement is provably one of the two - both are analyzed, not tainted.
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "IF 1 = 1 BEGIN SET @sql = N'SELECT 2'; END " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 1");
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 2");
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
    public void Scan_ExecOfVariableAfterTryCatchThatTouchesIt_BothOutcomeAssembliesAnalyzed()
    {
        // After the TRY/CATCH statement, exactly one of two fully-determined outcomes happened:
        // TRY ran to completion with no exception (tryDict's own value), or an exception
        // occurred and CATCH ran to completion (catchDict's value, itself built from the
        // pre-TRY baseline - see HandleTryCatch's own comment on why CATCH never starts from
        // tryDict). Both fold constant here, so both are analyzed rather than tainted.
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "BEGIN TRY SET @sql = N'SELECT 2'; END TRY " +
            "BEGIN CATCH SET @sql = N'SELECT 3'; END CATCH " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 2");
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 3");
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
    public void Scan_ExecOfVariableAfterUnrecognizedStatementNotMentioningIt_ProducesAnalyzableScript()
    {
        // A statement kind this scanner doesn't explicitly model (here, INSERT) can only have
        // mutated a variable it names literally - T-SQL locals cannot alias. This INSERT never
        // mentions @sql, so @sql must survive untainted.
        var result = Scan(
            "CREATE TABLE dbo.T (Col INT); " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "INSERT INTO dbo.T (Col) VALUES (1); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableAfterUnrecognizedStatementMentioningIt_Unanalyzable()
    {
        // Precision-first default: an unrecognized statement kind that DOES name the tracked
        // variable taints it, since this scanner can't rule out an unmodeled mechanism (e.g.
        // OUTPUT INTO) having changed it through that mention.
        var result = Scan(
            "CREATE TABLE dbo.T (Col NVARCHAR(MAX)); " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "INSERT INTO dbo.T (Col) VALUES (@sql); " +
            "EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("unsupported-statement-in-scope", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfUnrelatedVariableAfterUnrecognizedStatement_ProducesAnalyzableScript()
    {
        // The unrecognized statement mentions a DIFFERENT variable (@other) - only @other may
        // be tainted, @sql (never named by the INSERT) must survive.
        var result = Scan(
            "CREATE TABLE dbo.T (Col INT); " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "DECLARE @other INT = 1; " +
            "INSERT INTO dbo.T (Col) VALUES (@other); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
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
    public void Scan_ExecOfVariableReassignedInsideElseBranch_BothBranchAssembliesAnalyzed()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "IF 1 = 0 BEGIN SET @sql = N'SELECT 2'; END " +
            "ELSE BEGIN SET @sql = N'SELECT 3'; END " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 2");
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 3");
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
    public void Scan_ExecOfVariableUnrelatedToPriorProcCallWithOutput_ProducesAnalyzableScript()
    {
        // The unrecognized proc call only mutates @other (named as its OUTPUT argument) -
        // @sql, never mentioned by that call, must survive untainted.
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "DECLARE @other NVARCHAR(MAX); " +
            "EXEC dbo.BuildQuery @other OUTPUT; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
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
    public void Scan_ProcParamWithMultipleCallersPassingSameLiteral_ProducesAnalyzableScript()
    {
        // Value-seeding across proc-call edges (roadmap "trace provably-constant dynamic SQL
        // across proc-call edges", extended beyond a single caller): every known caller supplies
        // a literal for this parameter, so its runtime value is provably one of them - here both
        // callers happen to agree, so the assembly set collapses to one script.
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

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1 WHERE Status = 'Active'", script.InnerText);
    }

    [Fact]
    public void Scan_ProcParamWithMultipleCallersPassingDifferentLiterals_BothAssembliesAnalyzed()
    {
        var activeArgument = new ProcCallArgument(
            "@Status", null, false, null, true, new ProcCallLiteralArgument("Active", "caller.sql", 10, 30, PrefixLength: 2));
        var archivedArgument = new ProcCallArgument(
            "@Status", null, false, null, true, new ProcCallLiteralArgument("Archived", "caller.sql", 20, 30, PrefixLength: 2));
        var graph = new ProcCallGraph([
            new ProcCallEdge(null, CalleeProcName, new SourceSpan("caller.sql", 10, 5), [activeArgument]),
            new ProcCallEdge(null, CalleeProcName, new SourceSpan("caller.sql", 20, 5), [archivedArgument]),
        ]);

        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status NVARCHAR(20) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            graph);

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 1 WHERE Status = 'Active'");
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 1 WHERE Status = 'Archived'");
    }

    [Fact]
    public void Scan_ProcParamWithMultipleCallers_OneCallerNonLiteral_StaysTainted()
    {
        // Value-seeding requires EVERY known caller to supply a literal - a single non-literal
        // caller means the parameter's true value set is unknown, not merely wider than what the
        // OTHER callers' literals show, so this must not partially seed from the literal callers.
        var literalArgument = new ProcCallArgument(
            "@Status", null, false, null, true, new ProcCallLiteralArgument("Active", "caller.sql", 10, 30, PrefixLength: 2));
        var variableArgument = new ProcCallArgument("@Status", null, false, "@callerVar", IsLiteral: false, LiteralArgument: null);
        var graph = new ProcCallGraph([
            new ProcCallEdge(null, CalleeProcName, new SourceSpan("caller.sql", 10, 5), [literalArgument]),
            new ProcCallEdge(null, CalleeProcName, new SourceSpan("caller.sql", 20, 5), [variableArgument]),
        ]);

        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status NVARCHAR(20) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            graph);

        Assert.Empty(result.AnalyzableScripts);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("parameter-not-seeded:non-literal-caller", finding.Reason);
    }

    [Fact]
    public void Scan_ProcParamWithManyCallersPassingDistinctLiterals_CardinalityCapExceeded_StaysTainted()
    {
        // 40 distinct callers, each passing its own distinct literal - over the 32-assembly cap.
        var edges = Enumerable.Range(0, 40)
            .Select(i => new ProcCallEdge(
                null,
                CalleeProcName,
                new SourceSpan("caller.sql", 10 + i, 5),
                [new ProcCallArgument("@Status", null, false, null, true, new ProcCallLiteralArgument($"Status{i}", "caller.sql", 10 + i, 30, PrefixLength: 2))]))
            .ToList();

        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status NVARCHAR(20) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            new ProcCallGraph(edges));

        Assert.Empty(result.AnalyzableScripts);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("parameter-not-seeded:cardinality-cap", finding.Reason);
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
    public void Scan_ProcParamWithNoKnownCallers_ReasonNamesProcedureParameterNotUndeclaredVariable()
    {
        // Zero edges for this callee (application code, an unparsed caller, a synonym this scan
        // didn't resolve) - the parameter IS declared, just with no known caller to seed from, so
        // this must report its own honest reason rather than the misleading generic
        // "undeclared-variable" a caller-blind VariableReference lookup would otherwise produce.
        var graph = new ProcCallGraph([]);

        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status NVARCHAR(20) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            graph);

        Assert.Empty(result.AnalyzableScripts);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("procedure-parameter:no-known-call-site", finding.Reason);
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

    // ------------------------------------------------------------------
    // QUOTENAME folding (roadmap "fold high-volume string-builder functions in dynamic SQL,
    // oracle-checked") - every expected string below was verified directly against a live Docker
    // SQL Server instance, not assumed from documentation.
    // ------------------------------------------------------------------

    [Fact]
    public void Scan_ExecOfQuoteNameOnLiteral_DefaultBracketDelimiter_FoldsToBracketedText()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = 'SELECT * FROM ' + QUOTENAME(N'Orders'); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT * FROM [Orders]", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfQuoteNameOnFoldedVariable_TierC_FoldsToBracketedText()
    {
        // The realistic pattern this fold exists for: a variable that already folded constant
        // via straight-line DECLARE tracing, THEN wrapped in QUOTENAME.
        var result = Scan(
            "DECLARE @table VARCHAR(50) = 'Orders'; " +
            "DECLARE @sql VARCHAR(MAX) = 'SELECT * FROM ' + QUOTENAME(@table); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT * FROM [Orders]", script.InnerText);
    }

    // Real T-SQL's EXEC('...') string-list form only accepts a char literal, local variable, or
    // +-concatenation of those (ScriptDom rejects a bare function call there directly, verified:
    // "EXEC(QUOTENAME(...))" is itself a syntax error) - so every QUOTENAME scenario below routes
    // through a DECLARE assignment first, exactly like real dynamic SQL code has to.
    private static DynamicSqlExtractionResult ScanQuoteName(string quoteNameExpression) =>
        Scan($"DECLARE @sql NVARCHAR(MAX) = {quoteNameExpression}; EXEC(@sql);");

    [Fact]
    public void Scan_QuoteNameOnLiteral_EmbeddedCloseBracket_EscapesByDoubling()
    {
        var result = ScanQuoteName("QUOTENAME(N'ab]c')");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("[ab]]c]", script.InnerText);
    }

    [Fact]
    public void Scan_QuoteNameOnLiteral_EmbeddedOpenBracket_NeverEscaped()
    {
        // Only the CLOSING delimiter character is ever escaped - oracle-verified: QUOTENAME('ab[c')
        // returns "[ab[c]", not "[ab[[c]".
        var result = ScanQuoteName("QUOTENAME(N'ab[c')");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("[ab[c]", script.InnerText);
    }

    /// <summary>T-SQL source-text escaping (doubling an embedded single quote) for embedding an arbitrary string as a literal in a TEST's own generated SQL - unrelated to QUOTENAME's own escaping, which happens inside the engine once parsed.</summary>
    private static string AsSqlStringLiteral(string value) => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    [Theory]
    // inputWithEmbeddedCloseChar always contains the family's own close character so every case
    // actually exercises the doubling QUOTENAME itself performs, not just the wrap.
    [InlineData("'", "ab'c", "'ab''c'")]
    [InlineData("\"", "ab\"c", "\"ab\"\"c\"")]
    [InlineData("(", "ab)c", "(ab))c)")] // only ')' is the escaped close char for the paren family, doubled
    [InlineData("<", "ab>c", "<ab>>c>")]
    [InlineData("{", "ab}c", "{ab}}c}")]
    public void Scan_QuoteNameOnLiteral_RecognizedDelimiter_MatchesOracleEscaping(string delimiter, string inputWithEmbeddedCloseChar, string expected)
    {
        var result = ScanQuoteName(
            $"QUOTENAME({AsSqlStringLiteral(inputWithEmbeddedCloseChar)}, {AsSqlStringLiteral(delimiter)})");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(expected, script.InnerText);
    }

    [Fact]
    public void Scan_QuoteNameOnLiteral_UnrecognizedDelimiter_UnanalyzableWithNullResultReason()
    {
        // Oracle-verified: QUOTENAME('abc', 'x') returns SQL NULL for real (not brackets, not an
        // error) - concatenating NULL propagates NULL through the whole @sql build, which this
        // scanner has no representation for, so it must fail the fold rather than guess.
        var result = ScanQuoteName("QUOTENAME(N'abc', N'x')");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:quotename-null-result", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_QuoteNameOnLiteral_MultiCharacterDelimiter_UnanalyzableWithNullResultReason()
    {
        var result = ScanQuoteName("QUOTENAME(N'abc', N'ab')");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:quotename-null-result", finding.Reason);
    }

    [Fact]
    public void Scan_QuoteNameOnLiteral_InputOver128Characters_UnanalyzableWithNullResultReason()
    {
        // Oracle-verified boundary: QUOTENAME on a 128-character input still returns a real
        // value; 129 characters returns SQL NULL.
        var result = ScanQuoteName($"QUOTENAME(N'{new string('a', 129)}')");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:quotename-null-result", finding.Reason);
    }

    [Fact]
    public void Scan_QuoteNameOnLiteral_Input128Characters_Folds()
    {
        var input = new string('a', 128);
        var result = ScanQuoteName($"QUOTENAME(N'{input}')");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal($"[{input}]", script.InnerText);
    }

    [Fact]
    public void Scan_QuoteNameOnLiteral_EmptyDelimiter_DefaultsToBrackets()
    {
        var result = ScanQuoteName("QUOTENAME(N'abc', N'')");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("[abc]", script.InnerText);
    }

    [Fact]
    public void Scan_QuoteNameOnColumnReference_Unanalyzable()
    {
        // The argument itself can't fold - QUOTENAME's own result is then equally unknowable,
        // same as any other function call whose input isn't provably constant.
        var result = ScanQuoteName("QUOTENAME(SomeColumn)");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:column-reference", finding.Reason);
    }

    [Fact]
    public void Scan_QuoteNameWithThreeArguments_UnanalyzableAsFunctionCall()
    {
        // Not a real QUOTENAME overload - ScriptDom still parses it as a FunctionCall, and this
        // scanner declines rather than guessing which two of the three arguments matter.
        var result = ScanQuoteName("QUOTENAME(N'a', N'[', N'extra')");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:function-call", finding.Reason);
    }

    // ------------------------------------------------------------------
    // Whitelisted string-builder folding (roadmap "fold high-volume string-builder functions in
    // dynamic SQL, oracle-checked") - every expected string and every decline below was verified
    // directly against a live Docker SQL Server instance, not assumed from documentation.
    // ------------------------------------------------------------------

    [Fact]
    public void Scan_ExecOfVariableAssignedFromUpperOnAsciiLiteral_TierC_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = UPPER(N'select 1'); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedFromLowerOnAsciiLiteral_TierC_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = LOWER(N'SELECT 1'); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("select 1", script.InnerText);
    }

    [Theory]
    [InlineData("UPPER(N'select id')")] // contains 'i'
    [InlineData("UPPER(N'SELECT Id')")] // contains 'I'
    [InlineData("LOWER(N'select ID')")]
    public void Scan_CaseConversionOnInputContainingI_Declines_TurkishCollationAmbiguity(string expression)
    {
        // Oracle-verified: UPPER('i' COLLATE Turkish_CI_AS) is 'İ', not 'I' as under every other
        // collation family - the one ASCII letter pair whose case mapping genuinely depends on
        // collation. This scanner has no collation context at all, so it declines rather than
        // guessing which mapping the real target database uses.
        var result = Scan($"DECLARE @sql NVARCHAR(MAX) = {expression}; EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:case-conversion-collation-sensitive", finding.Reason);
    }

    [Fact]
    public void Scan_CaseConversionOnNonAsciiInput_Declines()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = UPPER(N'Ä'); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:case-conversion-collation-sensitive", finding.Reason);
    }

    [Fact]
    public void Scan_LtrimOnSpacePaddedLiteral_TrimsOnlySpace_NotTab()
    {
        // Oracle-verified: LTRIM/RTRIM trim ONLY the space character (0x20) - a leading tab is
        // left untouched, unlike .NET's parameterless TrimStart(), which strips all whitespace.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = LTRIM(N'  " + '\t' + "x'); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("\tx", script.InnerText);
    }

    [Fact]
    public void Scan_RtrimOnSpacePaddedLiteral_TrimsOnlySpace_NotTab()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = RTRIM(N'x" + '\t' + "  '); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("x\t", script.InnerText);
    }

    [Fact]
    public void Scan_LeftWithinBounds_TierC_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = LEFT(N'abcdef', 3); EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("abc", script.InnerText);
    }

    [Fact]
    public void Scan_LeftLengthBeyondInput_ClampsToWholeString()
    {
        // Oracle-verified: LEFT('abc', 10) returns 'abc' - no padding, no error.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = LEFT(N'abc', 10); EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("abc", script.InnerText);
    }

    [Fact]
    public void Scan_RightWithinBounds_TierC_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = RIGHT(N'abcdef', 3); EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("def", script.InnerText);
    }

    [Fact]
    public void Scan_LeftWithNegativeLength_Declines()
    {
        // Oracle-verified: LEFT with a negative length raises Msg 536 rather than returning
        // anything - the real EXEC would never reach this text on that path.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = LEFT(N'abc', -1); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:negative-length", finding.Reason);
    }

    [Fact]
    public void Scan_LeftWithNonLiteralLength_Declines()
    {
        // This scanner tracks only string variable values, never numeric ones - a length carried
        // in a variable is declined, not guessed.
        var result = Scan("DECLARE @n INT = 3; DECLARE @sql NVARCHAR(MAX) = LEFT(N'abcdef', @n); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:function-call-argument-diverges", finding.Reason);
    }

    [Fact]
    public void Scan_SubstringWithinBounds_TierC_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = SUBSTRING(N'abcdef', 2, 3); EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("bcd", script.InnerText);
    }

    [Fact]
    public void Scan_SubstringLengthBeyondInput_ClampsToRemainder()
    {
        // Oracle-verified: SUBSTRING('abcdef', 2, 100) returns 'bcdef' - clamped, not an error.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = SUBSTRING(N'abcdef', 2, 100); EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("bcdef", script.InnerText);
    }

    [Fact]
    public void Scan_SubstringStartBeyondInput_FoldsToEmptyString()
    {
        // Oracle-verified: SUBSTRING('abcdef', 10, 5) returns an empty string, not an error.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'X' + SUBSTRING(N'abcdef', 10, 5); EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("X", script.InnerText);
    }

    [Fact]
    public void Scan_SubstringWithNegativeLength_Declines()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = SUBSTRING(N'abcdef', 2, -1); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:negative-length", finding.Reason);
    }

    [Fact]
    public void Scan_SubstringWithStartBelowOne_Declines()
    {
        // Real, defined T-SQL behavior (oracle-verified: the window still clips against the
        // string's bounds), but rare enough outside adversarial input that this scanner declines
        // rather than adding the extra below-1 clipping arithmetic.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = SUBSTRING(N'abcdef', -2, 5); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:substring-start-below-one", finding.Reason);
    }

    [Fact]
    public void Scan_SubstringWithNonLiteralStart_Declines()
    {
        var result = Scan("DECLARE @n INT = 2; DECLARE @sql NVARCHAR(MAX) = SUBSTRING(N'abcdef', @n, 3); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:function-call-argument-diverges", finding.Reason);
    }

    [Fact]
    public void Scan_LeftOnFoldedVariable_TierC_ProducesAnalyzableScript()
    {
        // The realistic pattern this fold exists for: a variable that already folded constant
        // via straight-line DECLARE tracing, THEN wrapped in a whitelisted builder.
        var result = Scan(
            "DECLARE @table VARCHAR(50) = 'OrdersTable'; " +
            "DECLARE @sql VARCHAR(MAX) = 'SELECT * FROM ' + LEFT(@table, 6); " +
            "EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT * FROM Orders", script.InnerText);
    }

    // ------------------------------------------------------------------
    // REPLACE folding: agree-under-both-comparisons or decline. Every expected string and every
    // decline below was verified directly against a live Docker SQL Server instance.
    // ------------------------------------------------------------------

    [Fact]
    public void Scan_ReplaceWithNoCaseAmbiguity_TierC_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = REPLACE(N'a-b-c', N'-', N'_'); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("a_b_c", script.InnerText);
    }

    [Fact]
    public void Scan_ReplaceWhereOrdinalAndCaseInsensitiveDisagree_Declines()
    {
        // Oracle-verified: REPLACE('AbcABC','abc','X') is 'AbcABC' unchanged under an ordinal/
        // case-sensitive comparison (no exact "abc" substring present) but 'XX' under a
        // case-insensitive one - exactly the collation-dependent divergence this scanner has no
        // way to resolve without knowing the real target collation.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = REPLACE(N'AbcABC', N'abc', N'X'); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:replace-collation-sensitive", finding.Reason);
    }

    [Fact]
    public void Scan_ReplaceWithEmptyPattern_Declines()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = REPLACE(N'abc', N'', N'x'); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:replace-empty-pattern", finding.Reason);
    }

    [Fact]
    public void Scan_ReplaceWithWrongArgumentCount_UnanalyzableAsFunctionCall()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = REPLACE(N'abc', N'a'); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:function-call", finding.Reason);
    }

    // ------------------------------------------------------------------
    // CAST/CONVERT folding onto a VARCHAR(n)/NVARCHAR(n) target only - every non-string target
    // and CHAR/NCHAR's blank-padding declines rather than guessing a rendering.
    // ------------------------------------------------------------------

    [Fact]
    public void Scan_CastOfFoldedVariableToNVarcharWithTruncation_TierC_ProducesAnalyzableScript()
    {
        // Oracle-verified: CAST(N'HelloWorld' AS NVARCHAR(5)) silently truncates to 'Hello',
        // no error - the shape #5 in the audit ("caller passes a query string cast to nvarchar
        // before sp_executesql").
        var result = Scan(
            "DECLARE @raw NVARCHAR(MAX) = N'HelloWorld'; " +
            "DECLARE @sql NVARCHAR(MAX) = CAST(@raw AS NVARCHAR(5)); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("Hello", script.InnerText);
    }

    [Fact]
    public void Scan_ConvertOfLiteralToVarcharWithinLength_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = CONVERT(VARCHAR(20), N'SELECT 1'); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_CastToNonStringTarget_Declines()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = CAST(N'select 1' AS INT); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:cast-target-not-pinned", finding.Reason);
    }

    [Fact]
    public void Scan_CastToCharTarget_DeclinesBlankPaddingNotPinned()
    {
        // CHAR(n) blank-pads (oracle-verified: CAST('ab' AS char(5)) is 'ab   ') - a different,
        // unverified-here rendering from VARCHAR(n)'s plain truncation, so this declines rather
        // than guessing the padding.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = CAST(N'ab' AS CHAR(5)); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:cast-target-not-pinned", finding.Reason);
    }

    // ------------------------------------------------------------------
    // Non-deterministic builtins - genuinely unknowable at compile time, reported with their own
    // reason distinct from an ordinary unimplemented function call.
    // ------------------------------------------------------------------

    [Fact]
    public void Scan_ExecOfNewIdCastToString_Declines_NonDeterministicFunction()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'DROP TABLE tbl_' + CAST(NEWID() AS NVARCHAR(36)); " +
            "EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-deterministic-function", finding.Reason);
    }

    [Fact]
    public void Scan_ExecOfGetDate_Declines_NonDeterministicFunction()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = CONVERT(VARCHAR(30), GETDATE()); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-deterministic-function", finding.Reason);
    }

    // ------------------------------------------------------------------
    // CASE/IIF folding by unioning every branch - the discriminator/condition is never evaluated
    // at all, so this works even when it references a variable this scanner has no value for.
    // ------------------------------------------------------------------

    [Fact]
    public void Scan_IifWithBothBranchesLiteral_UnionsIntoTwoAssemblies()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = IIF(@flag = 1, N'SELECT A', N'SELECT B'); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var texts = result.AnalyzableScripts.Select(s => s.InnerText).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(["SELECT A", "SELECT B"], texts);
    }

    [Fact]
    public void Scan_SearchedCaseWithAllBranchesLiteral_UnionsAcrossEveryWhenAndElse()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = CASE " +
            "WHEN @flags & 1 = 1 THEN N'SELECT A' " +
            "WHEN @flags & 2 = 2 THEN N'SELECT B' " +
            "ELSE N'SELECT C' END; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var texts = result.AnalyzableScripts.Select(s => s.InnerText).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(["SELECT A", "SELECT B", "SELECT C"], texts);
    }

    [Fact]
    public void Scan_SearchedCaseWithNoElse_Declines()
    {
        // No ELSE means "no WHEN matched" returns SQL NULL, which this scanner's string-assembly
        // model has no representation for - omitting that outcome from the union would be
        // unsound, so it declines outright instead.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = CASE WHEN @flags = 1 THEN N'SELECT A' END; EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:conditional", finding.Reason);
    }

    [Fact]
    public void Scan_CaseWithOneUnfoldableBranch_Declines()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = CASE WHEN @flags = 1 THEN N'SELECT A' ELSE CONVERT(VARCHAR(30), GETDATE()) END; " +
            "EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:conditional", finding.Reason);
    }

    // ------------------------------------------------------------------
    // Branch-fold coverage (roadmap "trace provably-constant dynamic SQL across IF/ELSE/TRY-
    // CATCH branches") - the optional-filter accumulation pattern this scanner previously declined
    // outright is now analyzed as the union of every branch's own provably-constant assembly.
    // ------------------------------------------------------------------

    [Fact]
    public void Scan_ThreeWayIfElseIfElse_AllThreeBranchesFold_AllThreeAssembliesAnalyzed()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "DECLARE @mode INT = 0; " +
            "IF @mode = 0 BEGIN SET @sql = N'SELECT 2'; END " +
            "ELSE IF @mode = 1 BEGIN SET @sql = N'SELECT 3'; END " +
            "ELSE BEGIN SET @sql = N'SELECT 4'; END " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        Assert.Equal(3, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 2");
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 3");
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 4");
    }

    [Fact]
    public void Scan_TenIndependentOptionalFilters_CardinalityCapExceeded_Unanalyzable()
    {
        // 10 independent optional filters, each appending to @sql under its own IF with no ELSE,
        // produce up to 2^10 = 1024 possible assemblies - comfortably over the 32-assembly cap.
        var filters = string.Concat(Enumerable.Range(0, 10)
            .Select(i => $"IF @f{i} = 1 BEGIN SET @sql = @sql + N' AND c{i} = 1'; END "));
        var declares = string.Concat(Enumerable.Range(0, 10).Select(i => $"DECLARE @f{i} BIT = 0; "));

        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE 1 = 1'; " +
            declares +
            filters +
            "EXEC(@sql);");

        Assert.Empty(result.AnalyzableScripts);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("diverges-across-if-branches:cardinality-cap", finding.Reason);
    }

    [Fact]
    public void Scan_IfBranchOwnFoldFails_ElseBranchFine_MergedReasonIsBranchsOwn_NotDivergence()
    {
        // The THEN branch's own assignment can't fold (a function call this scanner doesn't
        // whitelist) - the merged state must carry THAT reason, not "diverges-across-if-branches",
        // even though the ELSE branch folded cleanly.
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "IF 1 = 1 BEGIN SET @sql = REVERSE(N'SELECT 2'); END " +
            "ELSE BEGIN SET @sql = N'SELECT 3'; END " +
            "EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:function-call", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_IfBranchesProduceByteIdenticalAssemblies_CollapseToOneScript()
    {
        // Both branches happen to assign the exact same literal text - the union must not report
        // the same defect twice just because two independent branches agree on it.
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "IF 1 = 1 BEGIN SET @sql = N'SELECT 2'; END " +
            "ELSE BEGIN SET @sql = N'SELECT 2'; END " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 2", script.InnerText);
    }
}
