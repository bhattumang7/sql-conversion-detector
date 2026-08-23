using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class IndexCoverageScannerTests
{
    private const string Ddl =
        "CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, A INT NOT NULL, B INT NOT NULL, C VARCHAR(50) NOT NULL);"
        + "CREATE NONCLUSTERED INDEX IX_A ON dbo.T(A);"
        + "CREATE TABLE dbo.T2 (Id INT NOT NULL PRIMARY KEY, A INT NOT NULL, B INT NOT NULL, C VARCHAR(50) NOT NULL);"
        + "CREATE NONCLUSTERED INDEX IX_T2_A_Covering ON dbo.T2(A) INCLUDE (B, C);"
        + "CREATE TABLE dbo.T3 (Id INT NOT NULL PRIMARY KEY, A INT NOT NULL, B INT NOT NULL);"
        + "CREATE NONCLUSTERED INDEX IX_T3_A_1 ON dbo.T3(A);"
        + "CREATE NONCLUSTERED INDEX IX_T3_A_2 ON dbo.T3(A) INCLUDE (B);";

    private static IReadOnlyList<IndexCoverageFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", $"{Ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return IndexCoverageScanner.Scan(result, catalog);
    }

    [Fact]
    public void NonCoveringSingleCandidateIndex_SeekWithResidualColumn_Fires()
    {
        var findings = Scan("SELECT Id, A, B, C FROM dbo.T WHERE A = 5;");

        var finding = Assert.Single(findings, f => f.Kind == IndexCoverageFindingKind.KeyLookupProneIndex);
        Assert.Equal("dbo.T", finding.TableQualifiedName);
        Assert.Equal("IX_A", finding.IndexName);
        Assert.Contains("B", finding.UncoveredColumns);
        Assert.Contains("C", finding.UncoveredColumns);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void CoveringIndex_NeverFires()
    {
        var findings = Scan("SELECT Id, A, B, C FROM dbo.T2 WHERE A = 5;");

        Assert.Empty(findings);
    }

    [Fact]
    public void QueryOnlyReferencingIndexedColumn_NeverFires()
    {

        var findings = Scan("SELECT A FROM dbo.T WHERE A = 5;");

        Assert.Empty(findings);
    }

    [Fact]
    public void TwoCandidateIndexesForSameLeadingColumn_NeverFires()
    {

        var findings = Scan("SELECT Id, A, B FROM dbo.T3 WHERE A = 5;");

        Assert.Empty(findings);
    }

    [Fact]
    public void NoPredicateConstrainingIndexedColumn_NeverFires()
    {
        var findings = Scan("SELECT Id, A, B, C FROM dbo.T;");

        Assert.Empty(findings);
    }

    [Fact]
    public void PredicateOnColumnWithNoIndexAtAll_NeverFires()
    {
        var findings = Scan("SELECT Id, A, B, C FROM dbo.T WHERE B = 5;");

        Assert.Empty(findings);
    }
}
