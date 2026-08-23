using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class TryCastComputedColumnPredicateScannerTests
{
    private static IReadOnlyList<TryCastComputedColumnPredicateFinding> Scan(string sql)
    {
        var ddl = """
            CREATE TABLE dbo.Events (
                Id INT NOT NULL PRIMARY KEY,
                RawDate VARCHAR(20) NULL,
                ParsedDate AS TRY_CAST(RawDate AS DATE),
                Amount INT NULL,
                RoundedAmount AS CAST(Amount AS BIGINT)
            );
            """;
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        var candidates = TryCastComputedColumnPredicateScanner.BuildCandidates(catalog);
        return TryCastComputedColumnPredicateScanner.Scan(result, catalog, candidates);
    }

    [Fact]
    public void TryCastComputedColumn_ReferencedInWhere_Fires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find AS BEGIN SELECT Id FROM dbo.Events WHERE ParsedDate = '2024-01-01'; END");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Events", finding.TableQualifiedName);
        Assert.Equal("ParsedDate", finding.ColumnName);
        Assert.Contains("TRY_CAST", finding.DefinitionText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void TryCastComputedColumn_ReferencedInJoinOn_Fires()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find AS
            BEGIN
                SELECT e.Id FROM dbo.Events e JOIN dbo.Events e2 ON e.ParsedDate = e2.ParsedDate;
            END
            """);

        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.Equal("ParsedDate", f.ColumnName));
    }

    [Fact]
    public void TryCastComputedColumn_ReferencedInHaving_Fires()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find AS
            BEGIN
                SELECT ParsedDate, COUNT(*) FROM dbo.Events GROUP BY ParsedDate HAVING ParsedDate > '2024-01-01';
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Events", finding.TableQualifiedName);
        Assert.Equal("ParsedDate", finding.ColumnName);
        Assert.Contains("TRY_CAST", finding.DefinitionText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void TryCastComputedColumn_NeverReferencedInPredicate_NeverFires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find AS BEGIN SELECT Id, ParsedDate FROM dbo.Events WHERE Amount > 0; END");

        Assert.Empty(findings);
    }

    [Fact]
    public void PlainCastComputedColumn_NeverFires()
    {

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find AS BEGIN SELECT Id FROM dbo.Events WHERE RoundedAmount = 5; END");

        Assert.Empty(findings);
    }

    [Fact]
    public void ComputedColumnWhoseDefinitionOnlyMentionsTryCastInAStringLiteral_NeverFires()
    {

        var ddl = """
            CREATE TABLE dbo.Labels (
                Id INT NOT NULL PRIMARY KEY,
                Kind VARCHAR(20) NULL,
                KindLabel AS ('TRY_CAST(' + Kind + ')')
            );
            """;
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\nCREATE PROCEDURE dbo.usp_Find AS BEGIN SELECT Id FROM dbo.Labels WHERE KindLabel = 'x'; END");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        var candidates = TryCastComputedColumnPredicateScanner.BuildCandidates(catalog);
        var findings = TryCastComputedColumnPredicateScanner.Scan(result, catalog, candidates);

        Assert.Empty(findings);
    }

    [Fact]
    public void OrdinaryColumn_NeverFires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find AS BEGIN SELECT Id FROM dbo.Events WHERE RawDate = '2024-01-01'; END");

        Assert.Empty(findings);
    }

    [Fact]
    public void CteSharesNameWithTheComputedColumnsRealTable_NeverFires()
    {

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find AS BEGIN " +
            "WITH Events AS (SELECT Id FROM dbo.Events) " +
            "SELECT Id FROM Events WHERE ParsedDate = '2024-01-01'; END");

        Assert.Empty(findings);
    }

    [Fact]
    public async Task LiveDeployment_TryCastComputedColumnInPredicate_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.TryCastPredicateTarget (Id INT NOT NULL PRIMARY KEY, RawDate VARCHAR(20) NULL);
            GO
            ALTER TABLE dbo.TryCastPredicateTarget ADD ParsedDate AS TRY_CAST(RawDate AS DATE);
            GO
            CREATE PROCEDURE dbo.usp_TryCastPredicateTarget_Find AS
            BEGIN
                SELECT Id FROM dbo.TryCastPredicateTarget WHERE ParsedDate = '2024-01-01';
            END
            """);

        var finding = Assert.Single(report.TryCastComputedColumnPredicateFindings);
        Assert.Equal("dbo.TryCastPredicateTarget", finding.TableQualifiedName);
        Assert.Equal("ParsedDate", finding.ColumnName);
    }

    [Fact]
    public async Task LiveDeployment_TryCastComputedColumnNeverReferenced_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.TryCastPredicateClean (Id INT NOT NULL PRIMARY KEY, RawDate VARCHAR(20) NULL);
            GO
            ALTER TABLE dbo.TryCastPredicateClean ADD ParsedDate AS TRY_CAST(RawDate AS DATE);
            GO
            CREATE PROCEDURE dbo.usp_TryCastPredicateClean_Find AS
            BEGIN
                SELECT Id, ParsedDate FROM dbo.TryCastPredicateClean WHERE Id > 0;
            END
            """);

        Assert.Empty(report.TryCastComputedColumnPredicateFindings);
    }
}
