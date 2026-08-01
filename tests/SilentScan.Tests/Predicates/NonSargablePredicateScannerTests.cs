using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Every rule fixture here is a real-world-sourced repro (see file header comments for
/// citations) per CLAUDE.md's "no invented corpus" rule, except where a fixture's own
/// comment explicitly documents that no real-world source could be found.
/// </summary>
public sealed class NonSargablePredicateScannerTests
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "tier1");

    private static IReadOnlyList<SargabilityFinding> ScanFixture(string fileName)
    {
        var path = Path.Combine(FixturesDir, fileName);
        var result = SqlScriptParser.ParseFile(path);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return NonSargablePredicateScanner.Scan(result);
    }

    [Fact]
    public void FunctionWrappedColumn_YearOnColumn_Fires()
    {
        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.FunctionWrappedColumn, finding.Kind);
        Assert.Equal("SomeDate", finding.ColumnName);
        Assert.Equal("YEAR", finding.Detail);
    }

    [Fact]
    public void FunctionWrappedColumn_IsNullOnColumn_Fires()
    {
        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_isnull_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.FunctionWrappedColumn, finding.Kind);
        Assert.Equal("Age", finding.ColumnName);
        Assert.Equal("ISNULL", finding.Detail);
    }

    [Fact]
    public void FunctionWrappedColumn_SargableDateRange_DoesNotFire()
    {
        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_clean.sql");

        Assert.Empty(findings);
    }

    [Fact]
    public void CastOnColumn_CastWrappingColumnInBetween_Fires()
    {
        var findings = ScanFixture("CAST_CONVERT_ON_COLUMN_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.CastOrConvertOnColumn, finding.Kind);
        Assert.Equal("CreatedDate", finding.ColumnName);
        Assert.Equal("CAST", finding.Detail);
    }

    [Fact]
    public void CastOnColumn_CastOnLiteralBounds_DoesNotFire()
    {
        var findings = ScanFixture("CAST_CONVERT_ON_COLUMN_clean.sql");

        Assert.Empty(findings);
    }

    [Fact]
    public void LeadingWildcardLike_PercentPrefix_Fires()
    {
        var findings = ScanFixture("LEADING_WILDCARD_LIKE_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.LeadingWildcardLike, finding.Kind);
        Assert.Equal("DisplayName", finding.ColumnName);
    }

    [Fact]
    public void LeadingWildcardLike_TrailingWildcardOnly_DoesNotFire()
    {
        var findings = ScanFixture("LEADING_WILDCARD_LIKE_clean.sql");

        Assert.Empty(findings);
    }

    [Fact]
    public void ColumnArithmetic_AddOnColumn_Fires()
    {
        var findings = ScanFixture("COLUMN_ARITHMETIC_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.ColumnArithmetic, finding.Kind);
        Assert.Equal("UnitPrice", finding.ColumnName);
        Assert.Equal("Add", finding.Detail);
    }

    [Fact]
    public void ColumnArithmetic_ArithmeticMovedToLiteralSide_DoesNotFire()
    {
        var findings = ScanFixture("COLUMN_ARITHMETIC_clean.sql");

        Assert.Empty(findings);
    }

    [Fact]
    public void LikePattern_Parameter_FiresAsNotLiteral()
    {
        var findings = ScanFixture("LIKE_PATTERN_NOT_LITERAL_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.LikePatternNotLiteral, finding.Kind);
        Assert.Equal("DisplayName", finding.ColumnName);
    }

    [Fact]
    public void WildcardColumnInFunction_HavingCountStar_DoesNotCrashAndDoesNotFire()
    {
        // Regression test for a real NullReferenceException found scanning
        // olahallengren/SQL-Server-Maintenance-Solution during the Phase 4 pilot - see the
        // fixture file header for the exact source line.
        var findings = ScanFixture("WILDCARD_COLUMN_IN_FUNCTION_clean.sql");

        Assert.Empty(findings);
    }

    [Fact]
    public void FunctionWrappedColumn_AggregateInHaving_DoesNotFire()
    {
        // docs/audit-remediation-plan.md Phase 3.1: confirmed false positive - SUM(Qty) in
        // HAVING was flagged as FunctionWrappedColumn before this fix, even though an
        // aggregate wrapping a grouped column is not an avoidable non-sargable transform.
        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_having_aggregate_clean.sql");

        Assert.Empty(findings);
    }

    [Fact]
    public void FunctionWrappedColumn_ScalarFunctionInHaving_StillFires()
    {
        // Near-miss sibling: a non-aggregate function wrapping a column in HAVING is exactly
        // as non-sargable as the same wrap in WHERE - the aggregate exclusion must be scoped
        // to aggregate function names, not to "any function call found in HAVING".
        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_having_scalar_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.FunctionWrappedColumn, finding.Kind);
        Assert.Equal("SomeDate", finding.ColumnName);
        Assert.Equal("YEAR", finding.Detail);
    }

    [Fact]
    public void FunctionWrappedColumn_InsideSelectListCase_DoesNotFire()
    {
        // docs/audit-remediation-plan.md Phase 3.1: confirmed false positive - a SELECT-list
        // CASE expression's WHEN condition was treated identically to a WHERE-clause predicate
        // before this fix, even though a SELECT-list computation has no seek to lose.
        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_select_list_case_clean.sql");

        Assert.Empty(findings);
    }

    [Fact]
    public void FunctionWrappedColumn_InJoinOnClause_StillFires()
    {
        // A JOIN's ON clause is a filter context exactly like WHERE - proves the context-gating
        // rewrite didn't regress ON-clause detection while excluding the SELECT list.
        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_join_on_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.FunctionWrappedColumn, finding.Kind);
        Assert.Equal("CreatedAt", finding.ColumnName);
        Assert.Equal("YEAR", finding.Detail);
    }
}
