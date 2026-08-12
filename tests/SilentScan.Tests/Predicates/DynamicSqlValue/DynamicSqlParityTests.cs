using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;

namespace SilentScan.Tests.Predicates.DynamicSqlValue;

/// <summary>
/// Regression coverage for real corpus-sweep findings the dynamic-SQL engine rebuild fixed along
/// the way - each test below documents an actual historical bug (see its own doc comment for the
/// originating corpus file) and asserts V2's own expected output directly. These used to compare
/// live against the old engine as well (the rebuild's Phase 3 parity gate); now that
/// the old engine is deleted, each test's literal expected value IS what that comparison proved
/// correct at the time, preserved here as a hardcoded regression fixture.
/// </summary>
public sealed class DynamicSqlParityTests
{
    private const string SourcePath = "test.sql";

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
    public void CursorFetchedTwice_FeedingCastInsideLoop_FoldsToTypedHole()
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

        var script = Assert.Single(DynamicSqlScannerV2.Scan(parseResult).AnalyzableScripts);
        Assert.Equal("KILL __silentscan_sym_L9C35__;", script.InnerText);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
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
    /// Since the ELSE branch's impure SELECT-assignment now degrades to a typed HavocWrite hole
    /// (@StringToExecute's declared NVARCHAR(MAX) survives the taint) rather than a bare taint,
    /// the ELSE side is no longer dropped outright: it joins the THEN side as a second Choice
    /// alternative, so the new engine now ALSO emits a second, Medium-confidence script standing
    /// in for the ELSE branch - strictly more coverage than the old engine's single recovered
    /// script, never less, so the divergence policy (new may analyze more than old, never less)
    /// still holds.
    /// </summary>
    [Fact]
    public void IfWithOneTaintedBranch_RecoversTheKnownBranchAndReportsTheOtherAsAHole()
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

        var newScripts = DynamicSqlScannerV2.Scan(parseResult).AnalyzableScripts;

        Assert.Equal(2, newScripts.Count);
        var recovered = Assert.Single(newScripts, s => s.InnerText == "INSERT INTO #Results SELECT HAVING COUNT(*) >  50  ORDER BY 1;");
        Assert.Equal(FindingConfidence.High, recovered.Confidence);
        var elseScript = Assert.Single(newScripts, s => s.InnerText != recovered.InnerText);
        Assert.Equal(FindingConfidence.Medium, elseScript.Confidence);
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

        var newScript = Assert.Single(DynamicSqlScannerV2.Scan(parseResult).AnalyzableScripts);

        Assert.Equal("INSERT [Cities_Archive] DEFAULT VALUES;", newScript.InnerText);
        Assert.Equal(FindingConfidence.High, newScript.Confidence);
    }
}
