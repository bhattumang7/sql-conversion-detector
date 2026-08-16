using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Catalog-only pass (docs/detection-checklist.md Tier 1 "Join-key and cross-object type/
/// collation mismatch": "column collation != database collation" / "temp-table collation vs.
/// database and tempdb collation") - no AST walking, no predicate site needed. Coverage-empty
/// against the local RM_ test database (its collation is uniform everywhere), so these fixtures
/// are directly authored to exercise the exact real-world pattern Erland Sommarskog's
/// widely-cited T-SQL collation FAQ (https://www.sommarskog.se/collating-sequences.html)
/// describes: a database migrated or created under a different default collation than its
/// individual columns, or a temp object whose implicit collation follows tempdb rather than the
/// user database.
/// </summary>
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
        // No explicit COLLATE - the column inherits the database default at CREATE TABLE time,
        // so there is nothing to report; the drift rule only fires on a genuine divergence.
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
        // The classic collation-conflict setup: a #temp table's column inherits tempdb's
        // collation, not the user database's, when no explicit COLLATE is given - but this
        // fixture gives an explicit COLLATE that differs from tempdb's own, exactly the case
        // that later joins against a user-database column risk a Msg 468 collation conflict on.
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
        // tempdb's own collation, when known, is the correct baseline for a temp object - NOT
        // the user database's, even though they commonly agree. A column matching tempdb but
        // differing from the user database must NOT fire.
        var findings = Scan(
            "CREATE TABLE #Staging (Code VARCHAR(20) COLLATE French_CI_AS NOT NULL);",
            databaseCollation: "SQL_Latin1_General_CP1_CI_AS",
            tempdbCollation: "French_CI_AS");

        Assert.Empty(findings);
    }

    [Fact]
    public void TempTableColumn_TempdbCollationUnknown_FallsBackToDatabaseDefault()
    {
        // DatabaseCatalog.EffectiveTempdbCollation's own documented fallback: when tempdb's
        // collation was never supplied, use the database's default instead of leaving temp
        // objects entirely unchecked.
        var findings = Scan(
            "CREATE TABLE #Staging (Code VARCHAR(20) COLLATE French_CI_AS NOT NULL);",
            databaseCollation: "SQL_Latin1_General_CP1_CI_AS",
            tempdbCollation: null);

        var finding = Assert.Single(findings);
        Assert.True(finding.IsTempObject);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", finding.BaselineCollationName);
    }
}
