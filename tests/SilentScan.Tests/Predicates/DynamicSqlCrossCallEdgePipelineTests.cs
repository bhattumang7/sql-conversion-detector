using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// End-to-end coverage for the roadmap item "trace provably-constant dynamic SQL across
/// proc-call edges": a caller passes a string literal into a callee procedure's parameter, and
/// the callee's OWN body builds dynamic SQL from that parameter. Before this, DynamicSqlScanner
/// never seeded a proc's own formal parameters at all - any reference to one inside dynamic SQL
/// failed as "undeclared-variable" regardless of what any caller passed. Runs through
/// ScanReportBuilder (ProcCallGraphBuilder -> DynamicSqlScanner -> DynamicSqlPipeline), the same
/// entry point production uses, not DynamicSqlScanner in isolation.
/// </summary>
public sealed class DynamicSqlCrossCallEdgePipelineTests
{
    private static ScanReport Scan(string sql)
    {
        var parseResult = SqlScriptParser.ParseText("dynsql_crosscall.sql", sql);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");
        foreach (var file in report.ParseHealth.Files)
        {
            Assert.Empty(file.Errors);
        }

        return report;
    }

    [Fact]
    public void SingleCallerLiteral_SeedsCalleeParameter_DynamicSqlAnalyzedAndScanForced()
    {
        // The seeded @Status value is reconstructed into the dynamic SQL text as an EXPLICIT
        // nvarchar literal (N'...' around the placeholder, not just around the STATIC pieces of
        // the concatenation) so the resulting comparison is varchar-column-vs-nvarchar-literal -
        // a genuine ScanForced this test can check for, rather than merely "some finding
        // exists". CLAUDE.md's own rule (only the reconstructed TEXT's own quote characters
        // determine a literal's type, never the outer nvarchar variable that built it) is
        // exactly why the DECLARE below embeds N' around @Status's own surrounding quotes.
        var report = Scan("""
            CREATE TABLE dbo.Orders (Status varchar(20) NOT NULL, INDEX IX_Status (Status));
            GO
            CREATE PROCEDURE dbo.usp_FindByStatus @Status NVARCHAR(20) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM dbo.Orders WHERE Status = N''' + @Status + N'''';
                EXEC(@sql);
            END;
            GO
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                EXEC dbo.usp_FindByStatus @Status = N'Active';
            END;
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Status");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.NotNull(finding.DynamicSqlCallSite);
        Assert.DoesNotContain(report.DynamicSqlFindings, f => f.Reason?.StartsWith("undeclared-variable", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void TwoCallersWithLiterals_NotSeeded_UnanalyzableWithMultipleCallSitesReason()
    {
        var report = Scan("""
            CREATE PROCEDURE dbo.usp_FindByStatus @Status NVARCHAR(20) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM dbo.Orders WHERE Status = ''' + @Status + N'''';
                EXEC(@sql);
            END;
            GO
            CREATE PROCEDURE dbo.usp_CallerA AS BEGIN EXEC dbo.usp_FindByStatus @Status = N'Active'; END;
            GO
            CREATE PROCEDURE dbo.usp_CallerB AS BEGIN EXEC dbo.usp_FindByStatus @Status = N'Closed'; END;
            """);

        var finding = Assert.Single(report.DynamicSqlFindings);
        Assert.Equal("parameter-not-seeded:multiple-call-sites", finding.Reason);
        Assert.Empty(report.TypedFindings);
    }

    [Fact]
    public void CallerPassesVariableNotLiteral_UnanalyzableWithNonLiteralCallerReason()
    {
        var report = Scan("""
            CREATE PROCEDURE dbo.usp_FindByStatus @Status NVARCHAR(20) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM dbo.Orders WHERE Status = ''' + @Status + N'''';
                EXEC(@sql);
            END;
            GO
            CREATE PROCEDURE dbo.usp_Caller @IncomingStatus NVARCHAR(20) AS
            BEGIN
                EXEC dbo.usp_FindByStatus @Status = @IncomingStatus;
            END;
            """);

        var finding = Assert.Single(report.DynamicSqlFindings);
        Assert.Equal("parameter-not-seeded:non-literal-caller", finding.Reason);
        Assert.Empty(report.TypedFindings);
    }
}
