using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates;

public sealed class ColumnCollationDriftScannerTests
{
    private static IReadOnlyList<ColumnCollationDriftFinding> Scan(string sql, string? databaseCollation = "SQL_Latin1_General_CP1_CI_AS", string? tempdbCollation = null)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        catalog.DefaultCollation = databaseCollation is null ? null : new Collation(databaseCollation);
        catalog.TempdbCollation = tempdbCollation is null ? null : new Collation(tempdbCollation);

        return ColumnCollationDriftScanner.Scan(catalog);
    }

    [Fact]
    public void ColumnCollationDiffersFromDatabaseDefault_Fires()
    {
        var findings = Scan("CREATE TABLE dbo.Customers (Code VARCHAR(20) COLLATE French_CI_AS NOT NULL);");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Customers", finding.TableQualifiedName);
        Assert.Equal("Code", finding.ColumnName);
        Assert.Equal("French_CI_AS", finding.ColumnCollationName);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", finding.BaselineCollationName);
        Assert.False(finding.IsTempObject);
    }

    [Fact]
    public void ColumnCollationMatchesDatabaseDefault_NeverFires()
    {
        var findings = Scan("CREATE TABLE dbo.Customers (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);");

        Assert.Empty(findings);
    }

    [Fact]
    public void ColumnWithNoExplicitCollate_InheritsDatabaseDefault_NeverFires()
    {

        var findings = Scan("CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL);");

        Assert.Empty(findings);
    }

    [Fact]
    public void NonStringColumn_NeverFires()
    {
        var findings = Scan("CREATE TABLE dbo.Customers (Id INT NOT NULL);");

        Assert.Empty(findings);
    }

    [Fact]
    public void DatabaseDefaultCollationUnresolved_NeverGuesses()
    {
        var findings = Scan("CREATE TABLE dbo.Customers (Code VARCHAR(20) COLLATE French_CI_AS NOT NULL);", databaseCollation: null);

        Assert.Empty(findings);
    }

    [Fact]
    public void TempTableColumnDiffersFromTempdbCollation_Fires()
    {

        var findings = Scan(
            "CREATE TABLE #Staging (Code VARCHAR(20) COLLATE French_CI_AS NOT NULL);",
            databaseCollation: "SQL_Latin1_General_CP1_CI_AS",
            tempdbCollation: "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(findings);
        Assert.Equal("Code", finding.ColumnName);
        Assert.True(finding.IsTempObject);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", finding.BaselineCollationName);
    }

    [Fact]
    public void TempTableColumn_BaselinesAgainstTempdbNotDatabaseCollation()
    {

        var findings = Scan(
            "CREATE TABLE #Staging (Code VARCHAR(20) COLLATE French_CI_AS NOT NULL);",
            databaseCollation: "SQL_Latin1_General_CP1_CI_AS",
            tempdbCollation: "French_CI_AS");

        Assert.Empty(findings);
    }

    [Fact]
    public void TempTableColumn_TempdbCollationUnknown_FallsBackToDatabaseDefault()
    {

        var findings = Scan(
            "CREATE TABLE #Staging (Code VARCHAR(20) COLLATE French_CI_AS NOT NULL);",
            databaseCollation: "SQL_Latin1_General_CP1_CI_AS",
            tempdbCollation: null);

        var finding = Assert.Single(findings);
        Assert.True(finding.IsTempObject);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", finding.BaselineCollationName);
    }
}
