using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Catch-all / kitchen-sink predicates" - the classic
/// "(Col = @p OR @p IS NULL)" optional-filter idiom (Erland Sommarskog, "Dynamic Search
/// Conditions in T-SQL"). Structural/AST tests for the extraction logic; the general
/// scan-forcing mechanism and the RECOMPILE-neutralizes-it claim are oracle-confirmed separately
/// in <see cref="CatchAllPredicateOracleTests"/>.
/// </summary>
public sealed class CatchAllPredicateScannerTests
{
    private static IReadOnlyList<CatchAllPredicateFinding> Scan(string sql)
    {
        var ddl = "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL, Region VARCHAR(20) NOT NULL, INDEX IX_Customers_Code (Code));";
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return CatchAllPredicateScanner.Scan(result, catalog);
    }

    [Fact]
    public void CanonicalOrder_ColumnEqualsParameterOrParameterIsNull_Fires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE (Code = @p OR @p IS NULL); END");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Customers", finding.TableQualifiedName);
        Assert.Equal("Code", finding.ColumnName);
        Assert.Equal("@p", finding.ParameterName);
        Assert.True(finding.Indexed);
    }

    [Fact]
    public void SwappedOrder_ParameterIsNullOrColumnEqualsParameter_Fires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE (@p IS NULL OR Code = @p); END");

        var finding = Assert.Single(findings);
        Assert.Equal("Code", finding.ColumnName);
    }

    [Fact]
    public void ChainedIndependentClauses_FiresOncePerMatchedPair()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20), @q VARCHAR(20) AS
            BEGIN
                SELECT 1 FROM dbo.Customers WHERE (Code = @p OR @p IS NULL) AND (Region = @q OR @q IS NULL);
            END
            """);

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.ColumnName == "Code" && f.ParameterName == "@p");
        Assert.Contains(findings, f => f.ColumnName == "Region" && f.ParameterName == "@q");
    }

    [Fact]
    public void CteSharesNameWithIndexedBaseTable_AttributesThroughToTheRealUnderlyingColumn()
    {
        // 2026-08 audit (the finding that started it): cteRelations was always null, so a CTE
        // named the same as dbo.Customers silently resolved against the real table's OWN "Code"
        // column instead of the CTE's actual body - firing table=dbo.Customers col=Code
        // indexed=True, a finding about the indexed column named in the OUTER query text but
        // never actually read (the CTE renames Region to Code; the real catch-all is on Region).
        // Fixed: the reference now resolves THROUGH the CTE to its true source - Region, not
        // Code, and Region is unindexed here, so the finding's own shape changes to match reality
        // rather than just disappearing (a CTE is never schema-qualified, so it always shadows a
        // same-named real base table, but the underlying read is still genuine and still reported
        // - correctly attributed, not incorrectly attributed the way the pre-fix bug was).
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS
            BEGIN
                WITH Customers AS (SELECT Region AS Code FROM dbo.Customers)
                SELECT 1 FROM Customers WHERE (Code = @p OR @p IS NULL);
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Customers", finding.TableQualifiedName);
        Assert.Equal("Region", finding.ColumnName);
        Assert.False(finding.Indexed);
    }

    [Fact]
    public void UnindexedColumn_StillFiresButReportsUnindexed()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE (Region = @p OR @p IS NULL); END");

        var finding = Assert.Single(findings);
        Assert.False(finding.Indexed);
    }

    [Fact]
    public void DeclaredLocalVariable_NotAFormalParameter_NeverFires()
    {
        // The whole premise ("one plan must serve every caller-supplied combination") only
        // holds for a genuinely caller-controlled formal parameter - a DECLARE'd local is a
        // single, fixed value for the whole compile and has no such story. This exact AST shape
        // belongs to LocalVariablePredicateFinding's own, separate concern instead.
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find AS BEGIN DECLARE @p VARCHAR(20) = 'ABC'; SELECT 1 FROM dbo.Customers WHERE (Code = @p OR @p IS NULL); END");

        Assert.Empty(findings);
    }

    [Fact]
    public void DifferentVariablesInEqualityAndIsNull_NeverFires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20), @q VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE (Code = @p OR @q IS NULL); END");

        Assert.Empty(findings);
    }

    [Fact]
    public void WrappedColumn_NeverFires()
    {
        // Precision guard: the compared column must be a bare ColumnReferenceExpression - a
        // wrapped column is the already-shipped Tier-1 sargability stream's own finding, and
        // stacking a second finding on the identical wrap would be noise, not signal.
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE (UPPER(Code) = @p OR @p IS NULL); END");

        Assert.Empty(findings);
    }

    [Fact]
    public void UnrelatedOr_NeverFires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20), @q VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE (Code = @p OR Region = @q); END");

        Assert.Empty(findings);
    }

    [Fact]
    public void PlainEquality_NoOr_NeverFires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE Code = @p; END");

        Assert.Empty(findings);
    }

    [Fact]
    public void NegatedCatchAllShape_NeverFires()
    {
        // NOT (Col = @p OR @p IS NULL) De Morgan's to Col &lt;&gt; @p AND @p IS NOT NULL - a
        // completely different, exclusion-shaped predicate with no "serve all NULL states" story.
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE NOT (Code = @p OR @p IS NULL); END");

        Assert.Empty(findings);
    }

    [Fact]
    public void StatementOptionRecompile_SuppressesTheFinding()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE (Code = @p OR @p IS NULL) OPTION (RECOMPILE); END");

        Assert.Empty(findings);
    }

    [Fact]
    public void ProcedureWithRecompile_SuppressesEveryFindingInTheBody()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) WITH RECOMPILE AS BEGIN SELECT 1 FROM dbo.Customers WHERE (Code = @p OR @p IS NULL); END");

        Assert.Empty(findings);
    }

    [Fact]
    public void UpdateStatement_SameShapeInWhereClause_Fires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN UPDATE dbo.Customers SET Region = 'X' WHERE (Code = @p OR @p IS NULL); END");

        var finding = Assert.Single(findings);
        Assert.Equal("Code", finding.ColumnName);
    }

    [Fact]
    public void AdHocBatchParameter_NoEnclosingProcedure_NeverFires()
    {
        // sp_executesql-seeded parameters aside (not exercised by this file-mode test), a bare
        // ad-hoc batch has no formal-parameter concept at all - a variable there is always a
        // DECLARE'd local, never a caller-supplied parameter.
        var findings = Scan("DECLARE @p VARCHAR(20) = 'ABC'; SELECT 1 FROM dbo.Customers WHERE (Code = @p OR @p IS NULL);");

        Assert.Empty(findings);
    }
}
