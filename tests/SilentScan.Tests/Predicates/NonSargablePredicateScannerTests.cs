using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
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

    private static IReadOnlyList<SargabilityFinding> ScanSql(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return NonSargablePredicateScanner.Scan(result);
    }

    private static IReadOnlyList<SargabilityFinding> ScanSqlWithCatalog(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        return NonSargablePredicateScanner.Scan(result, catalog, lineage);
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
    public void CastOnColumn_NoOpCastToSameCategory_DoesNotFire()
    {
        var sql = """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Quantity INT NOT NULL);
            GO
            CREATE INDEX IX_Orders_Quantity ON dbo.Orders(Quantity);
            GO
            SELECT OrderId FROM dbo.Orders WHERE CAST(Quantity AS INT) = 5;
            """;
        var findings = ScanSqlWithCatalog(sql);

        Assert.Empty(findings);
    }

    [Fact]
    public void ConvertOnColumn_NoOpConvertToSameCategory_DoesNotFire()
    {
        var sql = """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Quantity INT NOT NULL);
            GO
            CREATE INDEX IX_Orders_Quantity ON dbo.Orders(Quantity);
            GO
            SELECT OrderId FROM dbo.Orders WHERE CONVERT(INT, Quantity) = 5;
            """;
        var findings = ScanSqlWithCatalog(sql);

        Assert.Empty(findings);
    }

    [Fact]
    public void CastOnColumn_CastChangesCategory_StillFires()
    {
        var sql = """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Quantity INT NOT NULL);
            GO
            CREATE INDEX IX_Orders_Quantity ON dbo.Orders(Quantity);
            GO
            SELECT OrderId FROM dbo.Orders WHERE CAST(Quantity AS BIGINT) = 5;
            """;
        var findings = ScanSql(sql);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.CastOrConvertOnColumn, finding.Kind);
        Assert.Equal("Quantity", finding.ColumnName);
    }

    [Fact]
    public void CastOnColumn_NoOpCastOnArithmeticExpression_StillFires()
    {
        var sql = """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Quantity INT NOT NULL);
            GO
            CREATE INDEX IX_Orders_Quantity ON dbo.Orders(Quantity);
            GO
            SELECT OrderId FROM dbo.Orders WHERE CAST(Quantity + 1 AS INT) = 5;
            """;
        var findings = ScanSql(sql);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.CastOrConvertOnColumn, finding.Kind);
        Assert.Equal("Quantity", finding.ColumnName);
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
    public void LeadingWildcardLike_LeadingSingleCharWildcard_Fires()
    {
        var sql = """
            CREATE TABLE dbo.Users (UserId INT NOT NULL PRIMARY KEY, DisplayName NVARCHAR(40) NOT NULL);
            GO
            CREATE INDEX IX_Users_DisplayName ON dbo.Users(DisplayName);
            GO
            SELECT UserId FROM dbo.Users WHERE DisplayName LIKE '_ozar';
            """;
        var findings = ScanSql(sql);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.LeadingWildcardLike, finding.Kind);
        Assert.Equal("DisplayName", finding.ColumnName);
    }

    [Fact]
    public void LeadingWildcardLike_LeadingCharacterClassWildcard_Fires()
    {
        var sql = """
            CREATE TABLE dbo.Users (UserId INT NOT NULL PRIMARY KEY, DisplayName NVARCHAR(40) NOT NULL);
            GO
            CREATE INDEX IX_Users_DisplayName ON dbo.Users(DisplayName);
            GO
            SELECT UserId FROM dbo.Users WHERE DisplayName LIKE '[ab]ozar';
            """;
        var findings = ScanSql(sql);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.LeadingWildcardLike, finding.Kind);
        Assert.Equal("DisplayName", finding.ColumnName);
    }

    [Fact]
    public void LeadingWildcardLike_PatternHasEscapeClause_DoesNotFire()
    {
        var sql = """
            CREATE TABLE dbo.Users (UserId INT NOT NULL PRIMARY KEY, DisplayName NVARCHAR(40) NOT NULL);
            GO
            CREATE INDEX IX_Users_DisplayName ON dbo.Users(DisplayName);
            GO
            SELECT UserId FROM dbo.Users WHERE DisplayName LIKE '_ozar' ESCAPE '$';
            """;
        var findings = ScanSql(sql);

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

    [Fact]
    public void FunctionWrappedColumn_InMergeMatchedFilter_Fires()
    {
        var findings = ScanSql("""
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Notes VARCHAR(200) NOT NULL);
            CREATE TABLE dbo.OrdersStaging (OrderId INT NOT NULL PRIMARY KEY, Notes VARCHAR(200) NOT NULL);
            GO
            MERGE dbo.Orders AS target
            USING dbo.OrdersStaging AS source
            ON target.OrderId = source.OrderId
            WHEN MATCHED AND UPPER(target.Notes) = 'X' THEN
                UPDATE SET Notes = source.Notes;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.CaseFoldOnColumn, finding.Kind);
        Assert.Equal("Notes", finding.ColumnName);
    }

    [Fact]
    public void BetweenPredicate_OutsideFilterContext_DoesNotFire()
    {
        var findings = ScanSql("""
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Age INT NOT NULL);
            GO
            SELECT CASE WHEN CAST(Age AS BIGINT) BETWEEN 1 AND 10 THEN 1 ELSE 0 END FROM dbo.Orders;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void InPredicate_OutsideFilterContext_DoesNotFire()
    {
        var findings = ScanSql("""
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Age INT NOT NULL);
            GO
            SELECT CASE WHEN CAST(Age AS BIGINT) IN (1, 2) THEN 1 ELSE 0 END FROM dbo.Orders;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void LikePredicate_OutsideFilterContext_DoesNotFire()
    {
        var findings = ScanSql("""
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Notes VARCHAR(200) NOT NULL);
            GO
            SELECT CASE WHEN Notes LIKE '%X' THEN 1 ELSE 0 END FROM dbo.Orders;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void LikePredicate_FirstExpressionNotAColumnReference_DoesNotFireAsLikePattern()
    {
        var findings = ScanSql("""
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Notes VARCHAR(200) NOT NULL);
            GO
            SELECT OrderId FROM dbo.Orders WHERE UPPER(Notes) LIKE '%X';
            """);

        Assert.DoesNotContain(findings, f => f.Kind is SargabilityFindingKind.LeadingWildcardLike or SargabilityFindingKind.LikePatternNotLiteral);
    }

    [Fact]
    public void BetweenPredicate_InsideUnsatisfiableAndBranch_EliminatedByNormalization()
    {
        var findings = ScanSql("""
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Age INT NOT NULL);
            GO
            SELECT OrderId FROM dbo.Orders WHERE OrderId = 1 AND OrderId = 2 AND Age BETWEEN 1 AND 10;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void InPredicate_InsideUnsatisfiableAndBranch_EliminatedByNormalization()
    {
        var findings = ScanSql("""
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Age INT NOT NULL);
            GO
            SELECT OrderId FROM dbo.Orders WHERE OrderId = 1 AND OrderId = 2 AND CAST(Age AS BIGINT) IN (1, 2);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void LikePredicate_InsideUnsatisfiableAndBranch_EliminatedByNormalization()
    {
        var findings = ScanSql("""
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Notes VARCHAR(200) NOT NULL);
            GO
            SELECT OrderId FROM dbo.Orders WHERE OrderId = 1 AND OrderId = 2 AND Notes LIKE 'A%';
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void ColumnArithmetic_ParenthesizedColumnOperand_StillFinds()
    {
        var findings = ScanSql("""
            CREATE TABLE dbo.Products (ProductId INT NOT NULL PRIMARY KEY, UnitPrice DECIMAL(10,2) NOT NULL);
            GO
            SELECT ProductId FROM dbo.Products WHERE (UnitPrice) + 1 > 5;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.ColumnArithmetic, finding.Kind);
        Assert.Equal("UnitPrice", finding.ColumnName);
    }

    [Fact]
    public void ColumnArithmetic_UnaryNegatedColumnOperand_StillFinds()
    {
        var findings = ScanSql("""
            CREATE TABLE dbo.Products (ProductId INT NOT NULL PRIMARY KEY, UnitPrice DECIMAL(10,2) NOT NULL);
            GO
            SELECT ProductId FROM dbo.Products WHERE -UnitPrice + 1 > 5;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.ColumnArithmetic, finding.Kind);
        Assert.Equal("UnitPrice", finding.ColumnName);
    }

    [Fact]
    public void ColumnArithmetic_CastWrappedColumnOperand_StillFinds()
    {
        var findings = ScanSql("""
            CREATE TABLE dbo.Products (ProductId INT NOT NULL PRIMARY KEY, UnitPrice INT NOT NULL);
            GO
            SELECT ProductId FROM dbo.Products WHERE CAST(UnitPrice AS BIGINT) + 1 > 5;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.ColumnArithmetic, finding.Kind);
        Assert.Equal("UnitPrice", finding.ColumnName);
    }

    [Fact]
    public void ColumnArithmetic_ConvertWrappedColumnOperand_StillFinds()
    {
        var findings = ScanSql("""
            CREATE TABLE dbo.Products (ProductId INT NOT NULL PRIMARY KEY, UnitPrice INT NOT NULL);
            GO
            SELECT ProductId FROM dbo.Products WHERE CONVERT(BIGINT, UnitPrice) + 1 > 5;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.ColumnArithmetic, finding.Kind);
        Assert.Equal("UnitPrice", finding.ColumnName);
    }

    [Fact]
    public void ColumnArithmetic_NestedParenthesizedArithmeticOperand_StillFinds()
    {
        var findings = ScanSql("""
            CREATE TABLE dbo.Products (ProductId INT NOT NULL PRIMARY KEY, UnitPrice INT NOT NULL);
            GO
            SELECT ProductId FROM dbo.Products WHERE (UnitPrice + 1) + 2 > 5;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.ColumnArithmetic, finding.Kind);
        Assert.Equal("UnitPrice", finding.ColumnName);
    }

    [Fact]
    public void ColumnArithmetic_SimpleCaseWrappedColumnOperand_StillFinds()
    {
        var findings = ScanSql("""
            CREATE TABLE dbo.Products (ProductId INT NOT NULL PRIMARY KEY, UnitPrice INT NOT NULL);
            GO
            SELECT ProductId FROM dbo.Products WHERE (CASE UnitPrice WHEN 1 THEN 2 ELSE 3 END) + 1 > 5;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.ColumnArithmetic, finding.Kind);
        Assert.Equal("UnitPrice", finding.ColumnName);
    }

    [Fact]
    public void ColumnArithmetic_IifPredicateAndOfComparisonAndIsNull_FindsColumnThroughIsNullBranch()
    {
        var findings = ScanSql("""
            CREATE TABLE dbo.Products (ProductId INT NOT NULL PRIMARY KEY, UnitPrice INT NULL);
            GO
            SELECT ProductId FROM dbo.Products WHERE IIF(1 = 1 AND UnitPrice IS NULL, ProductId, 0) + 1 > 5;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.ColumnArithmetic, finding.Kind);
        Assert.Equal("UnitPrice", finding.ColumnName);
    }

    [Fact]
    public void ColumnArithmetic_IifPredicateNegatingAnUnmatchedShape_FallsThroughToThenExpression()
    {
        var findings = ScanSql("""
            CREATE TABLE dbo.Products (ProductId INT NOT NULL PRIMARY KEY, UnitPrice INT NOT NULL);
            GO
            SELECT ProductId FROM dbo.Products WHERE IIF(NOT (1 BETWEEN 1 AND 2), ProductId, 0) + 1 > 5;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.ColumnArithmetic, finding.Kind);
        Assert.Equal("ProductId", finding.ColumnName);
    }

    [Fact]
    public void CharindexOrLeftOnColumn_LeftWithNonLiteralLength_DoesNotFire()
    {
        var findings = ScanSql("""
            CREATE TABLE dbo.Products (ProductId INT NOT NULL PRIMARY KEY, Sku VARCHAR(50) NOT NULL);
            GO
            DECLARE @n INT = 3;
            SELECT ProductId FROM dbo.Products WHERE LEFT(Sku, @n) = 'ABC';
            """);

        Assert.Empty(findings);
    }
}
