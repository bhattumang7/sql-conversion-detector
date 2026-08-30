using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Reporting;

public sealed class ScanReportBuilderFindingAssemblyTests
{
    [Fact]
    public void MinimumConfidence_FiltersEachFindingByItsOwnConfidence_NotAllOrNothing()
    {
        const string Sql = """
            -- TODO: revisit this once the migration lands
            CREATE TABLE dbo.T (Col INT NOT NULL);
            GO
            SELECT 1 FROM dbo.T WHERE Col = NULL;
            """;

        var result = SqlScriptParser.ParseText("confidence.sql", Sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);

        var defaultReport = ScanReportBuilder.BuildFromParseResults([result], catalog);

        Assert.DoesNotContain(defaultReport.Find<DeprecatedSyntaxFinding>("DeprecatedSyntaxScanner"), f => f.Kind == DeprecatedSyntaxFindingKind.TaskCommentTodo);
        Assert.Contains(defaultReport.Find<DeprecatedSyntaxFinding>("DeprecatedSyntaxScanner"), f => f.Kind == DeprecatedSyntaxFindingKind.EqualsNullComparison);

        var lowConfidenceReport = ScanReportBuilder.BuildFromParseResults([result], catalog, FindingConfidence.Low);

        Assert.Contains(lowConfidenceReport.Find<DeprecatedSyntaxFinding>("DeprecatedSyntaxScanner"), f => f.Kind == DeprecatedSyntaxFindingKind.TaskCommentTodo);
        Assert.Contains(lowConfidenceReport.Find<DeprecatedSyntaxFinding>("DeprecatedSyntaxScanner"), f => f.Kind == DeprecatedSyntaxFindingKind.EqualsNullComparison);
    }

    [Fact]
    public void SeekPreservedVerdict_IsExcludedFromTypedFindingsButStillCountedInSummary_AndScanForcedOutranksUnknownDespiteAppearingLater()
    {
        const string Sql = """
            CREATE TABLE dbo.OrdersUnknownCol (Col INT NOT NULL);
            GO
            DECLARE @LeakedVar INT = 1; SELECT 1;
            GO
            SELECT 1 FROM dbo.OrdersUnknownCol WHERE Col = @LeakedVar;
            GO
            CREATE TABLE dbo.OrdersVariantCol (OrderId INT NOT NULL PRIMARY KEY, Tag SQL_VARIANT NOT NULL, INDEX IX_OrdersVariantCol_Tag (Tag));
            GO
            SELECT 1 FROM dbo.OrdersVariantCol WHERE Tag = 5;
            GO
            CREATE TABLE dbo.OrdersIntCol (OrderId INT NOT NULL PRIMARY KEY, Quantity INT NOT NULL, INDEX IX_OrdersIntCol_Quantity (Quantity));
            GO
            SELECT 1 FROM dbo.OrdersIntCol WHERE Quantity = CAST(5 AS SQL_VARIANT);
            """;

        var result = SqlScriptParser.ParseText("verdicts.sql", Sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);

        var report = ScanReportBuilder.BuildFromParseResults([result], catalog);

        Assert.DoesNotContain(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"), f => f.Verdict == Verdict.SeekPreserved);
        Assert.Equal(1, report.TypedPredicateSummary.SeekPreservedCount);

        var typedFindings = report.Find<TypedPredicateFinding>("TypedPredicateExtractor").ToList();
        var scanForcedIndex = typedFindings.FindIndex(f => f.Verdict == Verdict.ScanForced);
        var unknownIndex = typedFindings.FindIndex(f => f.Verdict == Verdict.Unknown);

        Assert.True(scanForcedIndex >= 0, "expected a ScanForced finding to survive filtering");
        Assert.True(unknownIndex >= 0, "expected an Unknown finding to survive filtering");
        Assert.True(
            scanForcedIndex < unknownIndex,
            "a ScanForced verdict must rank ahead of an Unknown verdict even though its source statement appears later in the file");
    }

    [Fact]
    public void EmptyParseResults_ProduceAnEmptyReportAcrossEveryStreamWithoutThrowing()
    {
        var report = ScanReportBuilder.BuildFromParseResults([], new DatabaseCatalog());

        Assert.Empty(report.ParseHealth.Files);
        Assert.Empty(report.Find<SargabilityFinding>("NonSargablePredicateScanner"));
        Assert.Empty(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"));
        Assert.Empty(report.Find<DynamicSqlFinding>("DynamicSqlScanner"));
        Assert.Empty(report.SkippedConstructs);
        Assert.Equal(0, report.TypedPredicateSummary.TotalClassified);
        Assert.Equal(0, report.DynamicSqlSummary.TotalCallSites);
        Assert.Equal(0, report.SkippedConstructSummary.TotalCount);
    }
}
