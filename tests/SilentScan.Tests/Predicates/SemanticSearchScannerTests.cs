using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class SemanticSearchScannerTests
{
    private static DatabaseCatalog CatalogWithSemanticColumns(IReadOnlyList<string>? semanticColumnNames)
    {
        var ddl = "CREATE TABLE dbo.Documents (DocumentId INT NOT NULL PRIMARY KEY, Body NVARCHAR(200) NULL, Summary NVARCHAR(200) NULL);";
        var result = SqlScriptParser.ParseText("test.sql", ddl);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);

        catalog.AddOrReplace(catalog.Find("dbo.Documents")! with
        {
            HasFullTextIndex = semanticColumnNames is not null,
            SemanticFullTextColumnNames = semanticColumnNames,
        });

        return catalog;
    }

    private static IReadOnlyList<SemanticSearchFinding> Scan(string sql, IReadOnlyList<string>? semanticColumnNames)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return SemanticSearchScanner.Scan(result, CatalogWithSemanticColumns(semanticColumnNames));
    }

    [Fact]
    public void TableWithNoFullTextIndexAtAll_FiresTableNotIndexed()
    {
        var findings = Scan(
            "SELECT k.* FROM SEMANTICKEYPHRASETABLE(dbo.Documents, Body, 1) AS k;",
            semanticColumnNames: null);

        var finding = Assert.Single(findings);
        Assert.Equal(SemanticSearchFindingKind.TableNotSemanticFullTextIndexed, finding.Kind);
        Assert.Null(finding.ColumnName);
    }

    [Fact]
    public void StarWithFullTextIndexButNoSemanticColumn_FiresTableNotIndexed()
    {
        var findings = Scan(
            "SELECT k.* FROM SEMANTICKEYPHRASETABLE(dbo.Documents, *, 1) AS k;",
            semanticColumnNames: []);

        var finding = Assert.Single(findings);
        Assert.Equal(SemanticSearchFindingKind.TableNotSemanticFullTextIndexed, finding.Kind);
    }

    [Fact]
    public void StarWithASemanticColumnPresent_NeverFires()
    {
        var findings = Scan(
            "SELECT k.* FROM SEMANTICKEYPHRASETABLE(dbo.Documents, *, 1) AS k;",
            semanticColumnNames: ["Body"]);

        Assert.Empty(findings);
    }

    [Fact]
    public void NamedColumnOnTableWithFullTextIndexButNoSemanticColumn_FiresColumnNotIndexed()
    {
        var findings = Scan(
            "SELECT k.* FROM SEMANTICKEYPHRASETABLE(dbo.Documents, Body, 1) AS k;",
            semanticColumnNames: []);

        var finding = Assert.Single(findings);
        Assert.Equal(SemanticSearchFindingKind.ColumnNotSemanticFullTextIndexed, finding.Kind);
        Assert.Equal("dbo.Documents", finding.TableQualifiedName);
        Assert.Equal("Body", finding.ColumnName);
    }

    [Fact]
    public void NamedColumnIsTheSemanticColumn_NeverFires()
    {
        var findings = Scan(
            "SELECT k.* FROM SEMANTICKEYPHRASETABLE(dbo.Documents, Body, 1) AS k;",
            semanticColumnNames: ["Body"]);

        Assert.Empty(findings);
    }

    [Fact]
    public void NamedColumnIsNotTheSemanticColumn_Fires()
    {
        var findings = Scan(
            "SELECT k.* FROM SEMANTICKEYPHRASETABLE(dbo.Documents, Summary, 1) AS k;",
            semanticColumnNames: ["Body"]);

        var finding = Assert.Single(findings);
        Assert.Equal(SemanticSearchFindingKind.ColumnNotSemanticFullTextIndexed, finding.Kind);
        Assert.Equal("dbo.Documents", finding.TableQualifiedName);
        Assert.Equal("Summary", finding.ColumnName);
    }

    [Fact]
    public void SemanticSimilarityDetailsTable_MatchedColumnNotSemantic_Fires()
    {
        var findings = Scan(
            "SELECT k.* FROM SEMANTICSIMILARITYDETAILSTABLE(dbo.Documents, Body, 1, Summary, 2) AS k;",
            semanticColumnNames: ["Body"]);

        var finding = Assert.Single(findings);
        Assert.Equal(SemanticSearchFindingKind.ColumnNotSemanticFullTextIndexed, finding.Kind);
        Assert.Equal("Summary", finding.ColumnName);
    }

    [Fact]
    public void UnresolvedColumnName_NeverFires()
    {
        var findings = Scan(
            "SELECT k.* FROM SEMANTICKEYPHRASETABLE(dbo.Documents, NotAColumn, 1) AS k;",
            semanticColumnNames: ["Body"]);

        Assert.Empty(findings);
    }

    [Fact]
    public void UnresolvedTable_NeverFires()
    {
        var findings = Scan(
            "SELECT k.* FROM SEMANTICKEYPHRASETABLE(dbo.NotATable, Body, 1) AS k;",
            semanticColumnNames: []);

        Assert.Empty(findings);
    }
}
