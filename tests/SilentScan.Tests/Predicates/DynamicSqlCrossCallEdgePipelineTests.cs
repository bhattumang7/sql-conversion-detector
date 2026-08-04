using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// End-to-end coverage for the roadmap item "trace provably-constant dynamic SQL across
/// proc-call edges": a caller passes a string literal into a callee procedure's parameter, and
/// the callee's OWN body builds dynamic SQL from that parameter. Before this, DynamicSqlScanner
/// never seeded a proc's own formal parameters at all - any reference to one inside dynamic SQL
/// failed as "variable-not-in-scope" regardless of what any caller passed. Runs through
/// ScanReportBuilder (ProcCallGraphBuilder -> DynamicSqlScanner -> DynamicSqlPipeline), the same
/// entry point production uses, not DynamicSqlScanner in isolation.
/// </summary>
public sealed class DynamicSqlCrossCallEdgePipelineTests
{
    private static async Task<ScanReport> Scan(string sql, FindingConfidence minimumConfidence = FindingConfidence.High)
    {
        var report = await EngineAuthoritativeScan.ScanAsync(sql, "SQL_Latin1_General_CP1_CI_AS", minimumConfidence: minimumConfidence);
        foreach (var file in report.ParseHealth.Files)
        {
            Assert.Empty(file.Errors);
        }

        return report;
    }

    [Fact]
    public async Task SingleCallerLiteral_SeedsCalleeParameter_DynamicSqlAnalyzedAndScanForced()
    {
        // The seeded @Status value is reconstructed into the dynamic SQL text as an EXPLICIT
        // nvarchar literal (N'...' around the placeholder, not just around the STATIC pieces of
        // the concatenation) so the resulting comparison is varchar-column-vs-nvarchar-literal -
        // a genuine ScanForced this test can check for, rather than merely "some finding
        // exists". CLAUDE.md's own rule (only the reconstructed TEXT's own quote characters
        // determine a literal's type, never the outer nvarchar variable that built it) is
        // exactly why the DECLARE below embeds N' around @Status's own surrounding quotes.
        var report = await Scan("""
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
        Assert.DoesNotContain(report.DynamicSqlFindings, f => f.Reason?.StartsWith("variable-not-in-scope", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task TwoCallersWithDifferentLiterals_BothAssembliesAnalyzed()
    {
        // Value-seeding across proc-call edges (extended beyond a single caller): every known
        // caller supplies a literal for @Status, so its runtime value is provably one of them -
        // both assemblies are reparsed and analyzed, rather than the whole site declining just
        // because there's more than one call site.
        var report = await Scan("""
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

        Assert.Equal(2, report.DynamicSqlFindings.Count(f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral));
        Assert.DoesNotContain(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);
        // dbo.Orders is never declared in this test's own DDL, so the reparsed predicate's column
        // never resolves to a real catalog table either way - unrelated to the seeding change.
        Assert.Empty(report.TypedFindings);
    }

    [Fact]
    public async Task NoKnownCaller_QuotedPlaceholderPosition_MediumConfidenceScanForced_ExcludedByDefault()
    {
        // Nothing anywhere in this snippet calls usp_FindByStatus - BuildParameterSeed's
        // zero-caller case seeds @Status as a symbolic placeholder of its own declared type
        // (NVARCHAR(20) resolves with no catalog needed) rather than tainting it, since T-SQL's
        // own type contract for this proc guarantees the runtime value really is this type. The
        // placeholder token lands inside a quoted N'...' literal in the generated SQL, so the
        // resulting comparison is a genuine varchar-column-vs-nvarchar-literal one BY
        // CONSTRUCTION - the same real ScanForced the fully-literal cross-call-edge test above
        // gets, just at Medium confidence (the runtime VALUE is unproven) instead of High.
        const string sql = """
            CREATE TABLE dbo.Orders (Status varchar(20) NOT NULL, INDEX IX_Status (Status));
            GO
            CREATE PROCEDURE dbo.usp_FindByStatus @Status NVARCHAR(20) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM dbo.Orders WHERE Status = N''' + @Status + N'''';
                EXEC(@sql);
            END;
            """;

        var defaultReport = await Scan(sql);
        Assert.DoesNotContain(defaultReport.TypedFindings, f => f.Column.ColumnName == "Status");

        var mediumReport = await Scan(sql, FindingConfidence.Medium);
        var finding = Assert.Single(mediumReport.TypedFindings, f => f.Column.ColumnName == "Status");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public async Task NoKnownCaller_ObjectIdentifierPosition_AnalyzedWithZeroFindings()
    {
        // @TableName lands entirely inside DROP TABLE's own object-name position - no value is
        // assumed anywhere (DROP TABLE has no predicate concept at all to miss), so this is
        // AnalyzedLiteral with zero findings even at the default (High) confidence threshold,
        // never Unanalyzable and never a fabricated finding about a table this scanner invented.
        var report = await Scan("""
            CREATE PROCEDURE dbo.usp_DropStagingTable @TableName SYSNAME AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'DROP TABLE ' + @TableName;
                EXEC(@sql);
            END;
            """);

        Assert.DoesNotContain(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);
        Assert.Contains(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral);
        Assert.Empty(report.TypedFindings);
        Assert.Empty(report.Tier1Findings);
    }

    [Fact]
    public async Task CallerPassesVariableNotLiteral_UnanalyzableWithNonLiteralCallerReason()
    {
        var report = await Scan("""
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
