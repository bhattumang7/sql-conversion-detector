using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;

namespace SilentScan.Tests.Predicates.DynamicSqlValue;

public sealed class DynamicSqlParityTests
{
    private const string SourcePath = "test.sql";

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
