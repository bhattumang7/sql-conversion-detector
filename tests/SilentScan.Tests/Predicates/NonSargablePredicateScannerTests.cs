using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

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
        Assert.Equal(SargabilityFindingKind.DateFunctionOnColumn, finding.Kind);
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
    public void FunctionWrappedColumn_IsNullSargableRewrite_DoesNotFire()
    {

        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_isnull_clean.sql");

        Assert.Empty(findings);
    }

    [Fact]
    public void FunctionWrappedColumn_CoalesceOnColumn_Fires()
    {
        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_coalesce_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.FunctionWrappedColumn, finding.Kind);
        Assert.Equal("ClosedDate", finding.ColumnName);
        Assert.Equal("COALESCE", finding.Detail);
    }

    [Fact]
    public void FunctionWrappedColumn_CoalesceSargableRewrite_DoesNotFire()
    {
        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_coalesce_clean.sql");

        Assert.Empty(findings);
    }

    [Fact]
    public void FunctionWrappedColumn_CaseWhenTestWrapsColumn_Fires()
    {

        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_case_when_test_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.FunctionWrappedColumn, finding.Kind);
        Assert.Equal("MobileNumber", finding.ColumnName);
        Assert.Equal("CASE", finding.Detail);
    }

    [Fact]
    public void FunctionWrappedColumn_CaseWhenTestSargableRewrite_DoesNotFire()
    {
        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_case_when_test_clean.sql");

        Assert.Empty(findings);
    }

    [Fact]
    public void FunctionWrappedColumn_CaseThenValueWrapsColumn_Fires()
    {
        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_case_then_value_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.FunctionWrappedColumn, finding.Kind);
        Assert.Equal("DomesticStatus", finding.ColumnName);
        Assert.Equal("CASE", finding.Detail);
    }

    [Fact]
    public void FunctionWrappedColumn_CaseThenValueSargableRewrite_DoesNotFire()
    {
        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_case_then_value_clean.sql");

        Assert.Empty(findings);
    }

    [Fact]
    public void FunctionWrappedColumn_NullIfOnColumn_Fires()
    {
        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_nullif_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.FunctionWrappedColumn, finding.Kind);
        Assert.Equal("Region", finding.ColumnName);
        Assert.Equal("NULLIF", finding.Detail);
    }

    [Fact]
    public void FunctionWrappedColumn_NullIfSargableRewrite_DoesNotFire()
    {
        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_nullif_clean.sql");

        Assert.Empty(findings);
    }

    [Fact]
    public void FunctionWrappedColumn_IifPredicateWrapsColumn_Fires()
    {
        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_iif_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.FunctionWrappedColumn, finding.Kind);
        Assert.Equal("MobileNumber", finding.ColumnName);
        Assert.Equal("IIF", finding.Detail);
    }

    [Fact]
    public void FunctionWrappedColumn_IifSargableRewrite_DoesNotFire()
    {
        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_iif_clean.sql");

        Assert.Empty(findings);
    }

    [Fact]
    public void CastOnColumn_CastOnLiteralBounds_DoesNotFire()
    {
        var findings = ScanFixture("CAST_CONVERT_ON_COLUMN_clean.sql");

        Assert.Empty(findings);
    }

    [Fact]
    public void FunctionWrappedColumn_JsonValueNoComputedColumn_Fires()
    {
        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_json_value_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.FunctionWrappedColumn, finding.Kind);
        Assert.Equal("Payload", finding.ColumnName);
        Assert.Equal("JSON_VALUE", finding.Detail);
    }

    [Fact]
    public void CaseFoldOnColumn_UpperWrapsColumn_Fires()
    {
        var findings = ScanFixture("CASE_FOLD_ON_COLUMN_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.CaseFoldOnColumn, finding.Kind);
        Assert.Equal("Email", finding.ColumnName);
        Assert.Contains("collation unresolved", finding.Detail);
    }

    [Fact]
    public void DateFunctionOnColumn_YearOnColumn_Fires()
    {
        var findings = ScanFixture("DATE_YEAR_ON_COLUMN_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.DateFunctionOnColumn, finding.Kind);
        Assert.Equal("OrderDate", finding.ColumnName);
        Assert.Equal("YEAR", finding.Detail);
    }

    [Fact]
    public void DateFunctionOnColumn_DateDiffOnColumn_Fires()
    {
        var findings = ScanFixture("DATE_DATEDIFF_ON_COLUMN_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.DateFunctionOnColumn, finding.Kind);
        Assert.Equal("LastActiveAt", finding.ColumnName);
        Assert.Equal("DATEDIFF", finding.Detail);
    }

    [Fact]
    public void CharindexOrLeftOnColumn_CharindexPrefixMatch_FiresWithRewriteAdvice()
    {
        var findings = ScanFixture("CHARINDEX_PREFIX_MATCH_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.CharindexOrLeftOnColumn, finding.Kind);
        Assert.Equal("Code", finding.ColumnName);
        Assert.Contains("rewritable to col LIKE", finding.Detail);
    }

    [Fact]
    public void CharindexOrLeftOnColumn_CharindexSubstringSearch_FiresWithNoRewriteAdvice()
    {
        var findings = ScanFixture("CHARINDEX_SUBSTRING_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.CharindexOrLeftOnColumn, finding.Kind);
        Assert.Equal("Code", finding.ColumnName);
        Assert.Contains("no sargable rewrite exists", finding.Detail);
    }

    [Fact]
    public void CharindexOrLeftOnColumn_LeftPrefixMatch_FiresWithRewriteAdvice()
    {
        var findings = ScanFixture("LEFT_PREFIX_MATCH_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.CharindexOrLeftOnColumn, finding.Kind);
        Assert.Equal("Sku", finding.ColumnName);
        Assert.Contains("rewritable to col LIKE", finding.Detail);
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
    public void ColumnArithmetic_ReversedOperandOrder_StillFires()
    {
        var sql = """
            CREATE TABLE dbo.Products (ProductId INT NOT NULL PRIMARY KEY, UnitPrice DECIMAL(10,2) NOT NULL);
            GO
            CREATE INDEX IX_Products_UnitPrice ON dbo.Products(UnitPrice);
            GO
            SELECT ProductId FROM dbo.Products WHERE 3.975 > UnitPrice + 1;
            """;
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var findings = NonSargablePredicateScanner.Scan(result);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.ColumnArithmetic, finding.Kind);
        Assert.Equal("UnitPrice", finding.ColumnName);
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
    public void LikePattern_LiteralWithoutLeadingWildcard_DoesNotFire()
    {

        var findings = ScanFixture("LIKE_PATTERN_NOT_LITERAL_clean.sql");

        Assert.Empty(findings);
    }

    [Fact]
    public void WildcardColumnInFunction_HavingCountStar_DoesNotCrashAndDoesNotFire()
    {

        var findings = ScanFixture("WILDCARD_COLUMN_IN_FUNCTION_clean.sql");

        Assert.Empty(findings);
    }

    [Fact]
    public void FunctionWrappedColumn_AggregateInHaving_DoesNotFire()
    {

        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_having_aggregate_clean.sql");

        Assert.Empty(findings);
    }

    [Fact]
    public void FunctionWrappedColumn_ScalarFunctionInHaving_StillFires()
    {

        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_having_scalar_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.DateFunctionOnColumn, finding.Kind);
        Assert.Equal("SomeDate", finding.ColumnName);
        Assert.Equal("YEAR", finding.Detail);
    }

    [Fact]
    public void FunctionWrappedColumn_HavingDateRange_DoesNotFire()
    {

        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_having_scalar_clean.sql");

        Assert.Empty(findings);
    }

    [Fact]
    public void FunctionWrappedColumn_InsideSelectListCase_DoesNotFire()
    {

        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_select_list_case_clean.sql");

        Assert.Empty(findings);
    }

    [Fact]
    public void FunctionWrappedColumn_InJoinOnClause_StillFires()
    {

        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_join_on_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.DateFunctionOnColumn, finding.Kind);
        Assert.Equal("CreatedAt", finding.ColumnName);
        Assert.Equal("YEAR", finding.Detail);
    }

    [Fact]
    public void FunctionWrappedColumn_JoinOnDateRange_DoesNotFire()
    {

        var findings = ScanFixture("FUNCTION_WRAPPED_COLUMN_join_on_clean.sql");

        Assert.Empty(findings);
    }
}
