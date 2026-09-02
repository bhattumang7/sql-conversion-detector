using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class FullTextIndexDdlScannerTests
{
    private static IReadOnlyList<FullTextIndexDdlFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return FullTextIndexDdlScanner.Scan(catalog);
    }

    [Fact]
    public void HexLanguageId_Invalid_Fires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Body NVARCHAR(200) NULL);
            CREATE FULLTEXT INDEX ON dbo.T(Body LANGUAGE 0x0F423F) KEY INDEX PK_T;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(FullTextIndexDdlFindingKind.InvalidLanguageId, finding.Kind);
    }

    [Fact]
    public void HexLanguageId_KnownLcid_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Body NVARCHAR(200) NULL);
            CREATE FULLTEXT INDEX ON dbo.T(Body LANGUAGE 0x409) KEY INDEX PK_T;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void NonNumericLanguageTerm_LeftUnchecked()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Body NVARCHAR(200) NULL);
            CREATE FULLTEXT INDEX ON dbo.T(Body LANGUAGE English) KEY INDEX PK_T;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void PersistedNonDeterministicComputedColumn_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Body VARCHAR(200) NULL, Checksummed AS (CONVERT(VARCHAR(20), CHECKSUM(Body))) PERSISTED);
            CREATE FULLTEXT INDEX ON dbo.T(Checksummed) KEY INDEX PK_T;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void DeterministicNonpersistedComputedColumn_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Body VARCHAR(200) NULL, Checksummed AS (CONVERT(VARCHAR(20), CHECKSUM(Body))));
            CREATE FULLTEXT INDEX ON dbo.T(Checksummed) KEY INDEX PK_T;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void SeededRand_TreatedAsDeterministic_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Body VARCHAR(200) NULL, Salted AS (Body + CONVERT(VARCHAR(20), RAND(1))));
            CREATE FULLTEXT INDEX ON dbo.T(Salted) KEY INDEX PK_T;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void GlobalVariableInComputedColumn_TreatedAsNonDeterministic()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Body VARCHAR(200) NULL, Stamped AS (Body + CONVERT(VARCHAR(20), @@SPID)));
            CREATE FULLTEXT INDEX ON dbo.T(Stamped) KEY INDEX PK_T;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(FullTextIndexDdlFindingKind.NonDeterministicComputedColumn, finding.Kind);
    }

    [Fact]
    public void MoreThan1024IndexedColumns_Fires()
    {
        var columnNames = Enumerable.Range(1, 1025).Select(i => $"Col{i}").ToList();
        var columnDefinitions = string.Join(", ", columnNames.Select(c => $"{c} NVARCHAR(50) NULL"));
        var indexColumnList = string.Join(", ", columnNames);

        var findings = Scan(
            $"""
            CREATE TABLE dbo.Wide (Id INT NOT NULL PRIMARY KEY, {columnDefinitions});
            CREATE FULLTEXT INDEX ON dbo.Wide({indexColumnList}) KEY INDEX PK_Wide;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(FullTextIndexDdlFindingKind.TooManyIndexedColumns, finding.Kind);
        Assert.Equal("dbo.Wide", finding.TableQualifiedName);
        Assert.Null(finding.ColumnName);
    }

    [Fact]
    public void ExactlyMaxIndexedColumns_NeverFires()
    {
        var columnNames = Enumerable.Range(1, 1024).Select(i => $"Col{i}").ToList();
        var columnDefinitions = string.Join(", ", columnNames.Select(c => $"{c} NVARCHAR(50) NULL"));
        var indexColumnList = string.Join(", ", columnNames);

        var findings = Scan(
            $"""
            CREATE TABLE dbo.Wide (Id INT NOT NULL PRIMARY KEY, {columnDefinitions});
            CREATE FULLTEXT INDEX ON dbo.Wide({indexColumnList}) KEY INDEX PK_Wide;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void UnresolvedTable_NeverFires()
    {
        var findings = Scan(
            """
            CREATE FULLTEXT INDEX ON dbo.NotATable(SomeColumn) KEY INDEX PK_NotATable;
            """);

        Assert.Empty(findings);
    }
}
