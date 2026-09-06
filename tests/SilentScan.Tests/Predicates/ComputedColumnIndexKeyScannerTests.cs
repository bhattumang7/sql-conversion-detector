using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class ComputedColumnIndexKeyScannerTests
{
    private static IReadOnlyList<ComputedColumnIndexKeyFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return ComputedColumnIndexKeyScanner.Scan(catalog);
    }

    [Fact]
    public void NondeterministicNonpersistedComputedColumn_KeyedByIndex_Fires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Body VARCHAR(200) NULL, Tagged AS (Body + CONVERT(VARCHAR(30), GETDATE())));
            CREATE INDEX IX_T_Tagged ON dbo.T(Tagged);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(ComputedColumnIndexKeyFindingKind.NonDeterministic, finding.Kind);
        Assert.Equal("Tagged", finding.ColumnName);
        Assert.Equal("IX_T_Tagged", finding.IndexName);
    }

    [Fact]
    public void ImpreciseNonpersistedComputedColumn_KeyedByIndex_Fires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, N FLOAT NOT NULL, ReadingText AS (CAST(SQRT(N) AS NVARCHAR(50))));
            CREATE INDEX IX_T_ReadingText ON dbo.T(ReadingText);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(ComputedColumnIndexKeyFindingKind.Imprecise, finding.Kind);
        Assert.Equal("ReadingText", finding.ColumnName);
    }

    [Fact]
    public void ImpreciseNonpersistedComputedColumn_KeyedByIncludeColumn_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, N FLOAT NOT NULL, ReadingText AS (CAST(SQRT(N) AS NVARCHAR(50))));
            CREATE INDEX IX_T_Id ON dbo.T(Id) INCLUDE (ReadingText);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void PersistedImpreciseComputedColumn_KeyedByIndex_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, N FLOAT NOT NULL, ReadingText AS (CAST(SQRT(N) AS NVARCHAR(50))) PERSISTED);
            CREATE INDEX IX_T_ReadingText ON dbo.T(ReadingText);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void PreciseDeterministicNonpersistedComputedColumn_KeyedByIndex_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Qty INT NOT NULL, Total AS (Id + Qty));
            CREATE INDEX IX_T_Total ON dbo.T(Total);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void NondeterministicAndImpreciseNonpersistedComputedColumn_ReportsNonDeterministicOnly()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, N FLOAT NOT NULL, W AS (CAST(SQRT(N) + RAND() AS NVARCHAR(50))));
            CREATE INDEX IX_T_W ON dbo.T(W);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(ComputedColumnIndexKeyFindingKind.NonDeterministic, finding.Kind);
    }

    [Fact]
    public void RegularColumn_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, N FLOAT NOT NULL);
            CREATE INDEX IX_T_N ON dbo.T(N);
            """);

        Assert.Empty(findings);
    }
}
