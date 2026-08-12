using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates.DynamicSqlValue;

/// <summary>
/// Runs representative scenarios through <see cref="DynamicSqlParityHarness"/> - the old and new
/// engines must agree, or the new engine must be strictly better, on every one
/// (docs/dynamic-sql-rebuild-plan.md Phase 3's exit gate). Scenarios here deliberately avoid
/// cross-procedure call-graph/OUTPUT-summary seeding, which V2 does not implement yet (a
/// documented, deferred precision gap - see DynamicSqlScannerV2's own doc comment) - every
/// scenario below exercises capability V2 SHOULD already match or beat.
/// </summary>
public sealed class DynamicSqlParityTests
{
    private const string SourcePath = "test.sql";

    public static TheoryData<string, string> Scenarios => new()
    {
        { "literal EXEC", "EXEC('SELECT * FROM Users');" },
        { "concatenated literal + variable", "DECLARE @tbl NVARCHAR(50) = 'Users'; EXEC('SELECT * FROM ' + @tbl);" },
        { "sp_executesql positional", "EXEC sp_executesql N'SELECT * FROM Users';" },
        { "sp_executesql with param decl", "EXEC sp_executesql N'SELECT * FROM Users WHERE Id = @Id', N'@Id INT', @Id = 5;" },
        { "IF/ELSE literal divergence", "DECLARE @sql NVARCHAR(MAX) = 'SELECT 1'; IF 1 = 1 SET @sql = 'SELECT 2'; EXEC(@sql);" },
        { "IF without ELSE", "DECLARE @sql NVARCHAR(MAX) = 'SELECT 1'; IF 1 = 1 SET @sql = 'SELECT 2'; EXEC(@sql);" },
        { "WHILE loop", "DECLARE @sql NVARCHAR(MAX) = 'SELECT 1'; DECLARE @i INT = 0; WHILE @i < 3 BEGIN SET @sql = 'SELECT 2'; SET @i += 1; END EXEC(@sql);" },
        { "TRY/CATCH", "DECLARE @sql NVARCHAR(MAX) = 'SELECT 1'; BEGIN TRY SET @sql = 'SELECT 2'; END TRY BEGIN CATCH SET @sql = 'SELECT 3'; END CATCH EXEC(@sql);" },
        { "GOTO skip", "DECLARE @sql NVARCHAR(MAX) = 'SELECT 1'; GOTO done; SET @sql = 'SELECT 2'; done: EXEC(@sql);" },
        { "UPPER builtin", "DECLARE @sql NVARCHAR(MAX) = 'SELECT ' + UPPER('abc'); EXEC(@sql);" },
        { "REPLACE builtin", "DECLARE @sql NVARCHAR(MAX) = 'SELECT * FROM ' + REPLACE('My-Table', '-', '_'); EXEC(@sql);" },
        { "QUOTENAME builtin", "DECLARE @tbl NVARCHAR(50) = 'Users'; DECLARE @sql NVARCHAR(MAX) = 'SELECT * FROM ' + QUOTENAME(@tbl); EXEC(@sql);" },
        { "SUBSTRING builtin", "DECLARE @sql NVARCHAR(MAX) = 'SELECT ' + SUBSTRING('abcdef', 2, 3); EXEC(@sql);" },
        { "CAST truncation", "DECLARE @sql NVARCHAR(MAX) = 'SELECT ' + CAST('abcdef' AS VARCHAR(3)); EXEC(@sql);" },
        { "LEFT/RIGHT builtins", "DECLARE @sql NVARCHAR(MAX) = 'SELECT ' + LEFT('abcdef', 3) + RIGHT('abcdef', 3); EXEC(@sql);" },
        { "CHAR/NCHAR builtins", "DECLARE @sql NVARCHAR(MAX) = 'SELECT ' + CHAR(65) + NCHAR(66); EXEC(@sql);" },
        { "uninitialized DECLARE placeholder", "DECLARE @x NVARCHAR(50); EXEC(@x);" },
        { "ISNULL first-arg-wins", "DECLARE @sql NVARCHAR(MAX) = 'SELECT ' + ISNULL('a', 'b'); EXEC(@sql);" },
        { "COALESCE first-arg-wins", "DECLARE @sql NVARCHAR(MAX) = 'SELECT ' + COALESCE('a', 'b'); EXEC(@sql);" },
        { "SearchedCase union", "DECLARE @sql NVARCHAR(MAX) = (CASE WHEN 1 = 1 THEN 'SELECT 1' ELSE 'SELECT 2' END); EXEC(@sql);" },
        { "SELECT-assignment pure", "DECLARE @x NVARCHAR(50); SELECT @x = 'literal'; EXEC(@x);" },
        { "SELECT-assignment impure declines", "DECLARE @x NVARCHAR(50); SELECT @x = name FROM sys.tables; EXEC(@x);" },
        { "unresolved variable EXEC", "EXEC(@sql);" },
        { "nested CREATE PROCEDURE", "CREATE PROCEDURE dbo.usp_Test AS BEGIN EXEC('SELECT 1'); END;" },
        { "stub-then-alter procedure", "CREATE PROCEDURE dbo.usp_Test AS BEGIN RETURN 0; END;\nGO\nALTER PROCEDURE dbo.usp_Test AS BEGIN EXEC('SELECT 1'); END;" },
        { "OUTPUT parameter proven constant", "CREATE PROCEDURE dbo.usp_Test @Result NVARCHAR(50) OUTPUT AS BEGIN SET @Result = 'fixed'; END;" },
        { "ordinary procedure call taints OUTPUT arg", "DECLARE @rc INT = 1; EXEC @rc = dbo.SomeProc @rc OUTPUT; DECLARE @sql NVARCHAR(MAX) = CAST(@rc AS NVARCHAR(10)); EXEC(@sql);" },
        { "FETCH INTO havoc", "DECLARE @x NVARCHAR(50) = 'before'; DECLARE cur CURSOR FOR SELECT name FROM sys.tables; FETCH NEXT FROM cur INTO @x; EXEC(@x);" },
        { "AddEquals concatenation", "DECLARE @sql NVARCHAR(MAX) = 'SELECT '; SET @sql += '1'; EXEC(@sql);" },
        { "NEWID nondeterministic typed", "DECLARE @sql NVARCHAR(MAX) = 'SELECT ' + REPLACE(CAST(NEWID() AS VARCHAR(36)), '-', ''); EXEC(@sql);" },
    };

    /// <summary>
    /// Regression test for a real corpus-sweep finding (SQL-Server-First-Responder-Kit's
    /// sp_kill.sql): a cursor variable FETCHed at TWO distinct call sites (once before a loop,
    /// once at the loop's own end) produced two structurally-different <c>HavocWrite</c> holes
    /// of the SAME type - <see cref="SqlTextValue.Join"/> merged them into a
    /// <c>Choice</c>-of-two-holes instead of recognizing they were equivalent, and a
    /// <c>Choice</c> piece isn't a bare <see cref="TemplatePiece.Hole"/>, so CAST's own
    /// hole-transfer silently declined a fold the old scanner completed. Fixed in
    /// <see cref="SqlTextValue.Join"/>: two same-type holes now collapse directly.
    /// </summary>
    [Fact]
    public void CursorFetchedTwice_FeedingCastInsideLoop_MatchesOldScanner()
    {
        var sql = """
            CREATE PROCEDURE dbo.sp_kill AS
            BEGIN
                DECLARE @CurrentSPID INT, @KillSQL NVARCHAR(100);
                DECLARE kill_cursor CURSOR FOR SELECT session_id FROM sys.dm_exec_sessions;
                OPEN kill_cursor;
                FETCH NEXT FROM kill_cursor INTO @CurrentSPID;
                WHILE @@FETCH_STATUS = 0
                BEGIN
                    SET @KillSQL = N'KILL ' + CAST(@CurrentSPID AS NVARCHAR(10)) + N';';
                    EXEC(@KillSQL);
                    FETCH NEXT FROM kill_cursor INTO @CurrentSPID;
                END
                CLOSE kill_cursor;
                DEALLOCATE kill_cursor;
            END;
            """;
        var parseResult = SqlScriptParser.ParseText(SourcePath, sql);
        Assert.False(parseResult.HasErrors, string.Join(';', parseResult.Errors.Select(e => e.Message)));

        var report = DynamicSqlParityHarness.Compare(parseResult);

        Assert.True(report.IsAcceptableUnderPolicy(out var violation), violation);
        var script = Assert.Single(SilentScan.Core.Predicates.DynamicSqlScanner.Scan(parseResult).AnalyzableScripts);
        var newScript = Assert.Single(DynamicSqlScannerV2.Scan(parseResult).AnalyzableScripts);
        Assert.Equal(script.InnerText, newScript.InnerText);
        Assert.Equal(script.Confidence, newScript.Confidence);
    }


    /// <summary>
    /// Regression test for a second real corpus-sweep finding (sp_Blitz.sql, CheckID 160): an IF
    /// whose THEN branch appends known literal text and whose ELSE branch is a
    /// <c>SELECT @v = expr FROM table</c> (always tainted - CLAUDE.md: corpus DML never
    /// executes). <see cref="SqlTextValue.Join"/> cannot represent "known on one side, unknown
    /// on the other" by itself, and its OWN uniform-declared-type recovery (both branches share
    /// @StringToExecute's declared NVARCHAR(MAX)) was silently downgrading the THEN branch's
    /// fully-known text to an opaque hole before <see cref="DynamicSqlCfg"/>'s
    /// guarded-alternative fixup ever got a chance to preserve it. Two bugs, both fixed: (1) the
    /// fixup now overrides Join's own choice for this exact shape rather than only patching an
    /// already-<see cref="SqlTextValue.Tainted"/> result; (2) it runs BEFORE the join block's own
    /// subsequent steps (a statement immediately following the IF, in the SAME block per
    /// <see cref="DynamicSqlCfg.BuildSequence"/>'s own doc comment, was consuming the stale value
    /// one statement too early). A follow-on `SET @sql = @sql + '...'` after the join also needed
    /// <see cref="ExpressionEvaluator.FoldConcatenation"/>'s tainted-left short-circuit removed -
    /// it was bypassing <see cref="SqlTextValue.Concat"/>'s own alternative-extension logic.
    /// </summary>
    [Fact]
    public void IfWithOneTaintedBranch_RecoversTheKnownBranchAsGuardedAlternative()
    {
        var sql = """
            CREATE PROCEDURE dbo.usp_Test AS
            BEGIN
                DECLARE @StringToExecute NVARCHAR(MAX);
                SET @StringToExecute = N'INSERT INTO #Results SELECT HAVING COUNT(*) > ';

                IF 50 > (SELECT COUNT(*) FROM sys.databases)
                    SET @StringToExecute = @StringToExecute + N' 50 ';
                ELSE
                    SELECT @StringToExecute = @StringToExecute + CAST(COUNT(*) * 2 AS NVARCHAR(50)) FROM sys.databases;

                SET @StringToExecute = @StringToExecute + N' ORDER BY 1;';

                EXECUTE(@StringToExecute);
            END;
            """;
        var parseResult = SqlScriptParser.ParseText(SourcePath, sql);
        Assert.False(parseResult.HasErrors, string.Join(';', parseResult.Errors.Select(e => e.Message)));

        var oldScript = Assert.Single(SilentScan.Core.Predicates.DynamicSqlScanner.Scan(parseResult).AnalyzableScripts);
        var newScript = Assert.Single(DynamicSqlScannerV2.Scan(parseResult).AnalyzableScripts);

        Assert.Equal("INSERT INTO #Results SELECT HAVING COUNT(*) >  50  ORDER BY 1;", oldScript.InnerText);
        Assert.Equal(oldScript.InnerText, newScript.InnerText);
        Assert.Equal(oldScript.Confidence, newScript.Confidence);
    }


    /// <summary>
    /// Regression test for a third real corpus-sweep finding (wide-world-importers'
    /// DeactivateTemporalTablesBeforeDataLoad.sql): <c>QUOTENAME(@TableName + '_' + @Suffix)</c> -
    /// a builtin argument that is itself a CONCATENATION of several literal variables, producing
    /// a MULTI-piece all-literal <see cref="SqlTextValue.Template"/> rather than one single Lit
    /// piece. <see cref="ExpressionEvaluator.ToBuiltinArgument"/>'s old pattern match only
    /// recognized a single-piece Template as a known value - any multi-piece one (even when every
    /// piece was Lit, genuinely fully known) fell through to
    /// <c>symbolic-value-in-function-argument</c>, wrongly declining a call this scanner could
    /// have folded completely. Cascaded through 17 near-identical CREATE TRIGGER blocks in the
    /// SAME procedure (85 scripts down to 17). Fixed by flattening every-piece-is-Lit regardless
    /// of piece count, matching the old scanner's own <c>TryFlatten</c>.
    /// </summary>
    [Fact]
    public void QuoteNameOverConcatenatedLiteralArgument_FoldsCompletely()
    {
        var sql = """
            DECLARE @TableName SYSNAME = N'Cities';
            DECLARE @Suffix NVARCHAR(MAX) = N'Archive';
            DECLARE @sql NVARCHAR(MAX) = N'INSERT ' + QUOTENAME(@TableName + N'_' + @Suffix) + N' DEFAULT VALUES;';
            EXEC(@sql);
            """;
        var parseResult = SqlScriptParser.ParseText(SourcePath, sql);
        Assert.False(parseResult.HasErrors, string.Join(';', parseResult.Errors.Select(e => e.Message)));

        var oldScript = Assert.Single(SilentScan.Core.Predicates.DynamicSqlScanner.Scan(parseResult).AnalyzableScripts);
        var newScript = Assert.Single(DynamicSqlScannerV2.Scan(parseResult).AnalyzableScripts);

        Assert.Equal("INSERT [Cities_Archive] DEFAULT VALUES;", oldScript.InnerText);
        Assert.Equal(oldScript.InnerText, newScript.InnerText);
        Assert.Equal(oldScript.Confidence, newScript.Confidence);
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void OldAndNewEngine_AgreeOrNewIsStrictlyBetter(string scenarioName, string sql)
    {
        var parseResult = SqlScriptParser.ParseText(SourcePath, sql);
        Assert.False(parseResult.HasErrors, $"[{scenarioName}] failed to parse: {string.Join(';', parseResult.Errors.Select(e => e.Message))}");

        var report = DynamicSqlParityHarness.Compare(parseResult);

        if (!report.IsAcceptableUnderPolicy(out var violation))
        {
            Assert.Fail($"[{scenarioName}] violates the divergence policy: {violation}");
        }
    }
}
