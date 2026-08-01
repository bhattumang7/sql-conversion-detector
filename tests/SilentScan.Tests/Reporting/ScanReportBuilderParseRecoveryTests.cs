using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;

namespace SilentScan.Tests.Reporting;

/// <summary>
/// docs/audit-remediation-plan.md Phase 4.4, audit finding B4: a file with one bad batch
/// previously vanished from catalog/lineage/predicates entirely, because
/// <see cref="ScanReportBuilder.BuildFromParseResults"/> excluded any parse result with a
/// non-empty Errors list, even though ScriptDOM itself had already dropped only the one
/// malformed batch and kept parsing the rest. "Done when: a file with one bad batch still
/// contributes its other batches' tables."
/// </summary>
public sealed class ScanReportBuilderParseRecoveryTests
{
    [Fact]
    public void FileWithOneBadBatch_OtherBatchesTableStillContributesToCatalog()
    {
        var result = SqlScriptParser.ParseText(
            "mixed.sql",
            """
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            GO
            CREATE TABLE dbo.Bad ((( THIS IS NOT VALID SYNTAX;
            GO
            CREATE PROCEDURE dbo.usp_Find @OrderCode NVARCHAR(20)
            AS
            BEGIN
                SELECT OrderCode FROM dbo.Orders WHERE OrderCode = @OrderCode;
            END
            GO
            """);

        Assert.True(result.HasErrors);
        Assert.Equal(2, result.BatchCount);

        var report = ScanReportBuilder.BuildFromParseResults([result]);

        var health = Assert.Single(report.ParseHealth.Files);
        Assert.NotEmpty(health.Errors);
        Assert.Equal(2, health.BatchCount);

        // The proc's own WHERE predicate against dbo.Orders is only classifiable if the table's
        // CREATE TABLE batch (unrelated to, and positioned around, the broken batch) still made
        // it into the catalog.
        var finding = Assert.Single(report.TypedFindings);
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
    }

    [Fact]
    public void FileWithNoSurvivingBatches_ContributesNothingButIsStillReportedInParseHealth()
    {
        var result = SqlScriptParser.ParseText("garbage.sql", "SELECT FROM WHERE;;;");

        Assert.True(result.HasErrors);
        Assert.Equal(0, result.BatchCount);

        var report = ScanReportBuilder.BuildFromParseResults([result]);

        var health = Assert.Single(report.ParseHealth.Files);
        Assert.NotEmpty(health.Errors);
        Assert.Equal(0, health.BatchCount);
        Assert.Empty(report.TypedFindings);
        Assert.Empty(report.Tier1Findings);
    }
}
