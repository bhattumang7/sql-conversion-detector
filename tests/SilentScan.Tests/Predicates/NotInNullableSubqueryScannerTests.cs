using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "NOT IN over a nullable subquery column" - the classic
/// three-valued-logic correctness trap real-world reports of this bug describe verbatim (e.g. the
/// SQLServerCentral thread "Subquery Returns No Rows when there are NULLs and 'NOT IN' is used").
/// Structural/AST tests for the extraction logic; the general result-set-corruption mechanism is
/// oracle-confirmed separately via real execution in <see cref="NotInNullableSubqueryOracleTests"/>.
/// </summary>
public sealed class NotInNullableSubqueryScannerTests
{
    private static IReadOnlyList<NotInNullableSubqueryFinding> Scan(string sql, string extraDdl = "")
    {
        var ddl =
            "CREATE TABLE dbo.Parent (Id INT NOT NULL);" +
            "CREATE TABLE dbo.ChildNullable (RefId INT NULL, INDEX IX_ChildNullable_RefId (RefId));" +
            "CREATE TABLE dbo.ChildNotNull (RefId INT NOT NULL);" +
            extraDdl;
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return NotInNullableSubqueryScanner.Scan(result, catalog);
    }

    [Fact]
    public void NullableSubqueryColumn_Fires()
    {
        var findings = Scan("SELECT Id FROM dbo.Parent WHERE Id NOT IN (SELECT RefId FROM dbo.ChildNullable);");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.ChildNullable", finding.SubqueryTableQualifiedName);
        Assert.Equal("RefId", finding.SubqueryColumnName);
        Assert.Equal("Id", finding.OuterColumnName);
        Assert.True(finding.SubqueryColumnIndexed);
    }

    [Fact]
    public void NotNullSubqueryColumn_NeverFires()
    {
        var findings = Scan("SELECT Id FROM dbo.Parent WHERE Id NOT IN (SELECT RefId FROM dbo.ChildNotNull);");

        Assert.Empty(findings);
    }

    [Fact]
    public void PlainIn_NotNegated_NeverFires()
    {
        var findings = Scan("SELECT Id FROM dbo.Parent WHERE Id IN (SELECT RefId FROM dbo.ChildNullable);");

        Assert.Empty(findings);
    }

    [Fact]
    public void LiteralValueList_NeverFires()
    {
        var findings = Scan("SELECT Id FROM dbo.Parent WHERE Id NOT IN (1, 2, 3);");

        Assert.Empty(findings);
    }

    [Fact]
    public void SubqueryWithDefensiveNotNullFilter_NeverFires()
    {
        var findings = Scan("SELECT Id FROM dbo.Parent WHERE Id NOT IN (SELECT RefId FROM dbo.ChildNullable WHERE RefId IS NOT NULL);");

        Assert.Empty(findings);
    }

    [Fact]
    public void SubqueryNotNullFilterOnlyReachableThroughOr_StillFires()
    {
        // "WHERE RefId IS NOT NULL OR SomeFlag = 1" does not unconditionally exclude NULLs from
        // every row the subquery could project - the OR branch can still let a NULL through.
        var findings = Scan(
            "SELECT Id FROM dbo.Parent WHERE Id NOT IN (SELECT RefId FROM dbo.ChildNullable WHERE RefId IS NOT NULL OR RefId = 1);");

        Assert.Single(findings);
    }

    [Fact]
    public void SubqueryNotNullFilterOnDifferentColumn_StillFires()
    {
        var findings = Scan(
            "SELECT Id FROM dbo.Parent WHERE Id NOT IN (SELECT RefId FROM dbo.ChildNullable WHERE Id2 IS NOT NULL);",
            extraDdl: "ALTER TABLE dbo.ChildNullable ADD Id2 INT NULL;");

        Assert.Single(findings);
    }

    [Fact]
    public void SubqueryProjectsExpression_NeverFires()
    {
        var findings = Scan("SELECT Id FROM dbo.Parent WHERE Id NOT IN (SELECT ISNULL(RefId, 0) FROM dbo.ChildNullable);");

        Assert.Empty(findings);
    }

    [Fact]
    public void SubqueryProjectsMultipleColumns_NeverFires()
    {
        // Not valid T-SQL for a real IN subquery (would be a runtime error), but the scanner
        // should not misbehave on it either - a defensive shape guard, not a claim this compiles.
        var findings = Scan("SELECT Id FROM dbo.Parent WHERE Id NOT IN (SELECT RefId, RefId FROM dbo.ChildNullable);");

        Assert.Empty(findings);
    }

    [Fact]
    public void NotExists_NeverFires()
    {
        var findings = Scan(
            "SELECT Id FROM dbo.Parent p WHERE NOT EXISTS (SELECT 1 FROM dbo.ChildNullable c WHERE c.RefId = p.Id);");

        Assert.Empty(findings);
    }

    [Fact]
    public void InPredicateInsideOrBranch_NeverFires()
    {
        var findings = Scan(
            "SELECT Id FROM dbo.Parent WHERE Id = 1 OR Id NOT IN (SELECT RefId FROM dbo.ChildNullable);");

        Assert.Empty(findings);
    }

    [Fact]
    public void UpdateStatement_SameShapeInWhereClause_Fires()
    {
        var findings = Scan("UPDATE dbo.Parent SET Id = Id WHERE Id NOT IN (SELECT RefId FROM dbo.ChildNullable);");

        Assert.Single(findings);
    }

    [Fact]
    public void OuterSideIsExpression_StillFiresWithNullOuterColumnName()
    {
        var findings = Scan("SELECT Id FROM dbo.Parent WHERE (Id + 1) NOT IN (SELECT RefId FROM dbo.ChildNullable);");

        var finding = Assert.Single(findings);
        Assert.Null(finding.OuterColumnName);
    }
}
