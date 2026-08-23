using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Diagnostics;

public sealed class SkipLedgerTests
{
    [Fact]
    public void SkipLedger_Record_AppendsEntryWithGivenFields()
    {
        var ledger = new SkipLedger();

        ledger.Record(AnalysisPass.Predicates, "test.sql", 3, 7, "kind", "reason");

        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(AnalysisPass.Predicates, entry.Pass);
        Assert.Equal("test.sql", entry.SourcePath);
        Assert.Equal(3, entry.Line);
        Assert.Equal(7, entry.Column);
        Assert.Equal("kind", entry.ConstructKind);
        Assert.Equal("reason", entry.Reason);
    }

    [Fact]
    public void CatalogBuilder_AlterTableBeforeCreateTable_TwoPhaseBuildResolvesRegardlessOfOrder()
    {
        var alterFirst = SqlScriptParser.ParseText("02_alter.sql", "ALTER TABLE dbo.Users ADD Email VARCHAR(200) NULL;");
        var createSecond = SqlScriptParser.ParseText("01_create.sql", "CREATE TABLE dbo.Users (Id INT NOT NULL);");

        var catalog = CatalogBuilder.Build([alterFirst, createSecond]);

        Assert.Empty(catalog.Skipped.Entries);
        Assert.NotNull(catalog.Find("dbo.Users")!.FindColumn("Email"));
    }

    [Fact]
    public void CatalogBuilder_CreateIndexBeforeCreateTable_TwoPhaseBuildResolvesRegardlessOfOrder()
    {
        var indexFirst = SqlScriptParser.ParseText("02_index.sql", "CREATE INDEX IX_Users_Email ON dbo.Users(Email);");
        var createSecond = SqlScriptParser.ParseText("01_create.sql", "CREATE TABLE dbo.Users (Id INT NOT NULL, Email VARCHAR(200) NULL);");

        var catalog = CatalogBuilder.Build([indexFirst, createSecond]);

        Assert.Empty(catalog.Skipped.Entries);
        Assert.True(catalog.Find("dbo.Users")!.IsIndexedColumn("Email"));
    }

    [Fact]
    public void CatalogBuilder_AlterOrIndexWithNoMatchingTableAnywhere_StillRecordsSkip()
    {
        var alterOnly = SqlScriptParser.ParseText("test.sql", "ALTER TABLE dbo.Ghost ADD Email VARCHAR(200) NULL;");

        var catalog = CatalogBuilder.Build([alterOnly]);

        var entry = Assert.Single(catalog.Skipped.Entries);
        Assert.Equal(AnalysisPass.Catalog, entry.Pass);
        Assert.Contains("dbo.Ghost", entry.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogBuilder_WellOrderedDdl_RecordsNoSkips()
    {
        var create = SqlScriptParser.ParseText("01_create.sql", "CREATE TABLE dbo.Users (Id INT NOT NULL, Email VARCHAR(200) NULL);");
        var alter = SqlScriptParser.ParseText("02_alter.sql", "ALTER TABLE dbo.Users ADD Phone VARCHAR(20) NULL;");
        var index = SqlScriptParser.ParseText("03_index.sql", "CREATE INDEX IX_Users_Email ON dbo.Users(Email);");

        var catalog = CatalogBuilder.Build([create, alter, index]);

        Assert.Empty(catalog.Skipped.Entries);
    }

    [Fact]
    public void LineageResolver_ViewOverUnknownTable_RecordsSkip()
    {
        var view = SqlScriptParser.ParseText("view.sql", "CREATE VIEW dbo.vw_Ghost AS SELECT g.Id FROM dbo.Ghost AS g;");

        var catalog = CatalogBuilder.Build([view]);
        var lineage = LineageResolver.Resolve(catalog, [view]);

        Assert.Equal(2, lineage.Skipped.Entries.Count);
        Assert.All(lineage.Skipped.Entries, e => Assert.Equal(AnalysisPass.Lineage, e.Pass));
        Assert.Contains(lineage.Skipped.Entries, e => e.ConstructKind == "FROM table reference" && e.Reason.Contains("dbo.Ghost", StringComparison.Ordinal));
        Assert.Contains(lineage.Skipped.Entries, e => e.ConstructKind == "column reference");
    }

    [Fact]
    public void LineageResolver_CyclicView_RecordsSkip()
    {
        var viewA = SqlScriptParser.ParseText("a.sql", "CREATE VIEW dbo.vw_A AS SELECT b.Id FROM dbo.vw_B AS b;");
        var viewB = SqlScriptParser.ParseText("b.sql", "CREATE VIEW dbo.vw_B AS SELECT a.Id FROM dbo.vw_A AS a;");

        var catalog = CatalogBuilder.Build([viewA, viewB]);
        var lineage = LineageResolver.Resolve(catalog, [viewA, viewB]);

        Assert.Contains(lineage.Skipped.Entries, e => e.ConstructKind == "view dependency");
    }

    [Fact]
    public void TypedPredicateExtractor_UpdateWhereClause_ResolvesInsteadOfSkipping()
    {
        var sql = """
            CREATE TABLE dbo.Users (Id INT NOT NULL, DisplayName VARCHAR(40) NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_RenameUser @Id NVARCHAR(40)
            AS
            BEGIN
                UPDATE dbo.Users SET DisplayName = 'x' WHERE Id = @Id;
            END
            """;
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors);

        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        var extraction = TypedPredicateExtractor.Extract(result, catalog, lineage);

        Assert.Empty(extraction.SkippedConstructs);
        var finding = Assert.Single(extraction.TypedFindings);
        Assert.Equal("dbo.Users", finding.Column.TableQualifiedName);
        Assert.Equal("Id", finding.Column.ColumnName);
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
    }

    [Fact]
    public void TypedPredicateExtractor_BareIfComparison_StillRecordsSkip()
    {
        var sql = """
            CREATE PROCEDURE dbo.usp_Check @Flag INT
            AS
            BEGIN
                IF @Flag = 1
                BEGIN
                    RETURN;
                END
            END
            """;
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors);

        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        var extraction = TypedPredicateExtractor.Extract(result, catalog, lineage);

        Assert.Empty(extraction.TypedFindings);
        Assert.Contains(extraction.SkippedConstructs, e => e.ConstructKind == "comparison outside FROM scope");
    }

    [Fact]
    public void TypedPredicateExtractor_OrdinaryWhereClause_RecordsNoSkip()
    {
        var sql = "CREATE TABLE dbo.Users (Id INT NOT NULL);\nGO\nSELECT Id FROM dbo.Users WHERE Id = 1;";
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors);

        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        var extraction = TypedPredicateExtractor.Extract(result, catalog, lineage);

        Assert.Empty(extraction.SkippedConstructs);
    }

    [Fact]
    public void ScanReportBuilder_AggregatesSkippedConstructsAcrossAllPasses()
    {
        var sql = """
            CREATE TABLE dbo.Users (Id INT NOT NULL, DisplayName VARCHAR(40) NOT NULL);
            GO
            CREATE VIEW dbo.vw_Ghost AS SELECT g.Id FROM dbo.Ghost AS g;
            GO
            CREATE PROCEDURE dbo.usp_RenameUser @Id NVARCHAR(40)
            AS
            BEGIN
                UPDATE dbo.Users SET DisplayName = 'x' WHERE Id = @Id;
                IF @Id = N'0'
                BEGIN
                    RETURN;
                END
            END
            """;

        var parseResult = SqlScriptParser.ParseText("skip_ledger.sql", sql);
        Assert.Empty(parseResult.Errors);
        var catalog = CatalogBuilder.Build([parseResult]);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], catalog);

        Assert.Contains(report.SkippedConstructs, e => e.Pass == AnalysisPass.Lineage);
        Assert.Contains(report.SkippedConstructs, e => e.Pass == AnalysisPass.Predicates);
        Assert.Equal(report.SkippedConstructs.OrderBy(e => e.Pass).ThenBy(e => e.SourcePath, StringComparer.Ordinal).ThenBy(e => e.Line), report.SkippedConstructs);
    }

    [Fact]
    public void DatabaseCatalog_MergeFileModeExtras_CarriesFileModeCatalogsSkippedEntriesForward()
    {
        var alterOnly = SqlScriptParser.ParseText("test.sql", "ALTER TABLE dbo.Ghost ADD Email VARCHAR(200) NULL;");
        var fileModeCatalog = CatalogBuilder.Build([alterOnly]);
        Assert.Single(fileModeCatalog.Skipped.Entries);

        var liveCatalog = new DatabaseCatalog();
        liveCatalog.MergeFileModeExtras(fileModeCatalog);

        var entry = Assert.Single(liveCatalog.Skipped.Entries);
        Assert.Equal(AnalysisPass.Catalog, entry.Pass);
        Assert.Contains("dbo.Ghost", entry.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedPredicateExtractor_UnrecognizedComparisonOperator_NeverThrows()
    {
        var sql = "CREATE TABLE dbo.T (Col INT NOT NULL);\nGO\nSELECT Col FROM dbo.T WHERE Col = 1;";
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors);

        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);

        var exception = Record.Exception(() => TypedPredicateExtractor.Extract(result, catalog, lineage));

        Assert.Null(exception);
    }
}
