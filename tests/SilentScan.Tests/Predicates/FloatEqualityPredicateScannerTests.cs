using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §A "float/real as an
/// equality-predicate target" - the AST-level half; see <see cref="FloatEqualityFinding"/> for the
/// full scope/precision story, including this scanner's own known v1 scope limit (direct
/// base-table alias resolution only, no view/CTE/lineage resolution).
/// </summary>
public sealed class FloatEqualityPredicateScannerTests
{
    private static IReadOnlyList<FloatEqualityFinding> Scan(string sql, string extraDdl = "")
    {
        var ddl =
            "CREATE TABLE dbo.Prices (Id INT NOT NULL PRIMARY KEY, Amount FLOAT NOT NULL, Rate REAL NOT NULL, Name VARCHAR(50) NOT NULL);" +
            "CREATE TABLE dbo.Other (Id INT NOT NULL PRIMARY KEY, Amount FLOAT NOT NULL);" +
            // CREATE VIEW must be the first statement in its own batch - GO-separate any extra DDL
            // (a view, in practice) from the plain CREATE TABLE statements above.
            (extraDdl.Length > 0 ? $"\nGO\n{extraDdl}" : string.Empty);
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return FloatEqualityPredicateScanner.Scan(result, catalog);
    }

    [Fact]
    public void EqualityAgainstFloatColumn_LiteralOnRight_Fires()
    {
        var findings = Scan("SELECT * FROM dbo.Prices WHERE Amount = 1.5;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Prices", finding.TableQualifiedName);
        Assert.Equal("Amount", finding.ColumnName);
    }

    [Fact]
    public void EqualityAgainstRealColumn_Fires()
    {
        var findings = Scan("SELECT * FROM dbo.Prices WHERE Rate = 1.5;");

        var finding = Assert.Single(findings);
        Assert.Equal("Rate", finding.ColumnName);
    }

    [Fact]
    public void EqualityAgainstFloatColumn_LiteralOnLeft_Fires()
    {
        var findings = Scan("SELECT * FROM dbo.Prices WHERE 1.5 = Amount;");

        Assert.Single(findings);
    }

    [Fact]
    public void EqualityAgainstFloatColumn_QualifiedByAlias_Fires()
    {
        var findings = Scan("SELECT * FROM dbo.Prices p WHERE p.Amount = 1.5;");

        Assert.Single(findings);
    }

    [Fact]
    public void EqualityAgainstFloatColumn_InJoinOnClause_Fires()
    {
        var findings = Scan(
            "SELECT * FROM dbo.Prices p JOIN dbo.Other o ON p.Amount = o.Amount WHERE p.Id = o.Id;");

        // One finding per predicate site, even though both sides resolve to a float column.
        Assert.Single(findings);
    }

    [Fact]
    public void EqualityAgainstFloatColumn_InUpdateWhere_Fires()
    {
        var findings = Scan("UPDATE dbo.Prices SET Name = 'x' WHERE Amount = 1.5;");

        Assert.Single(findings);
    }

    [Fact]
    public void EqualityAgainstFloatColumn_InDeleteWhere_Fires()
    {
        var findings = Scan("DELETE FROM dbo.Prices WHERE Amount = 1.5;");

        Assert.Single(findings);
    }

    [Fact]
    public void PositionedUpdateWhereCurrentOfCursor_NullSearchCondition_NeverThrows()
    {
        // A real crash caught scanning the local test database: WHERE CURRENT OF @cursor carries
        // a WhereClause with a null SearchCondition (not a boolean expression at all) - this must
        // never throw, and obviously never fires (there is no comparison to find).
        var findings = Scan(
            "DECLARE cur CURSOR FOR SELECT Id FROM dbo.Prices; "
            + "OPEN cur; FETCH NEXT FROM cur; "
            + "UPDATE dbo.Prices SET Name = 'x' WHERE CURRENT OF cur; "
            + "CLOSE cur; DEALLOCATE cur;");

        Assert.Empty(findings);
    }

    [Fact]
    public void PositionedDeleteWhereCurrentOfCursor_NullSearchCondition_NeverThrows()
    {
        var findings = Scan(
            "DECLARE cur CURSOR FOR SELECT Id FROM dbo.Prices; "
            + "OPEN cur; FETCH NEXT FROM cur; "
            + "DELETE FROM dbo.Prices WHERE CURRENT OF cur; "
            + "CLOSE cur; DEALLOCATE cur;");

        Assert.Empty(findings);
    }

    [Fact]
    public void EqualityAgainstNonFloatColumn_NeverFires()
    {
        var findings = Scan("SELECT * FROM dbo.Prices WHERE Id = 1;");

        Assert.Empty(findings);
    }

    [Fact]
    public void RangeComparisonAgainstFloatColumn_NeverFires()
    {
        // Only a top-level `=` is examined - see this type's own doc comment for why range
        // operators are out of this v1's scope.
        var findings = Scan("SELECT * FROM dbo.Prices WHERE Amount > 1.5;");

        Assert.Empty(findings);
    }

    [Fact]
    public void EqualityAgainstFloatColumn_InsideSubquery_ResolvesWithSubqueryOwnScope()
    {
        var findings = Scan(
            "SELECT * FROM dbo.Other o WHERE o.Id IN (SELECT p.Id FROM dbo.Prices p WHERE p.Amount = 1.5);");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Prices", finding.TableQualifiedName);
    }

    [Fact]
    public void EqualityAgainstFloatColumn_ThroughView_NotAnalyzed()
    {
        // Known v1 scope limit - only a direct base-table alias is resolved, never a view.
        var findings = Scan(
            "SELECT * FROM dbo.PricesView WHERE Amount = 1.5;",
            extraDdl: "CREATE VIEW dbo.PricesView AS SELECT Amount FROM dbo.Prices;");

        Assert.Empty(findings);
    }
}
