using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Regression coverage for three Tier-1 structural holes formerly pinned in
/// KnownGapCharacterizationTests (function-wrapped column inside an IN predicate, as a BETWEEN
/// bound, and CAST wrapping an expression that merely contains a column rather than IS one):
/// NonSargablePredicateScanner previously visited only WHERE/HAVING/JOIN ON comparisons and
/// BETWEEN's tested value, and matched CAST/CONVERT/arithmetic only against a DIRECT
/// ColumnReferenceExpression operand. Synthetic scenarios exercising the scanner's traversal
/// itself, not new rule-correctness fixtures (CLAUDE.md's real-world-sourced fixture rule
/// applies to tier1/ RULEID_fires/_clean pairs, not to this kind of structural coverage - same
/// distinction FullPipelineSyntheticMiniProjectTests and
/// NonSargablePredicateScannerIndexResolutionTests already draw).
/// </summary>
public sealed class Tier1StructuralCoverageTests
{
    private static IReadOnlyList<SargabilityFinding> ScanWithCatalog(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        return NonSargablePredicateScanner.Scan(result, catalog, lineage);
    }

    [Fact]
    public void FunctionWrappedColumn_InsideInPredicate_Fires()
    {
        var findings = ScanWithCatalog("""
            CREATE TABLE dbo.Users (UserName varchar(50) NOT NULL);
            CREATE INDEX IX_Users_UserName ON dbo.Users(UserName);
            GO
            SELECT 1 FROM dbo.Users WHERE UPPER(UserName) IN ('ALICE', 'BOB');
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.FunctionWrappedColumn, finding.Kind);
        Assert.Equal("UserName", finding.ColumnName);
        Assert.Equal("dbo.Users", finding.TableQualifiedName);
        Assert.True(finding.Indexed);
    }

    [Fact]
    public void BareColumnInsideInPredicate_DoesNotFire()
    {
        var findings = ScanWithCatalog("""
            CREATE TABLE dbo.Users (UserName varchar(50) NOT NULL);
            GO
            SELECT 1 FROM dbo.Users WHERE UserName IN ('ALICE', 'BOB');
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void FunctionWrappedColumn_AsBetweenBound_Fires()
    {
        var findings = ScanWithCatalog("""
            CREATE TABLE dbo.Ranges (LowCode varchar(10) NOT NULL, HighCode varchar(10) NOT NULL);
            CREATE INDEX IX_Ranges_LowCode ON dbo.Ranges(LowCode);
            GO
            SELECT 1 FROM dbo.Ranges WHERE 'm' BETWEEN LOWER(LowCode) AND HighCode;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.FunctionWrappedColumn, finding.Kind);
        Assert.Equal("LowCode", finding.ColumnName);
        Assert.Equal("LOWER", finding.Detail);
        Assert.True(finding.Indexed);
    }

    [Fact]
    public void BareColumnsOnBothBetweenBounds_DoesNotFire()
    {
        var findings = ScanWithCatalog("""
            CREATE TABLE dbo.Ranges (LowCode varchar(10) NOT NULL, HighCode varchar(10) NOT NULL);
            GO
            SELECT 1 FROM dbo.Ranges WHERE 'm' BETWEEN LowCode AND HighCode;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void CastWrappingExpressionThatContainsAColumn_Fires()
    {
        var findings = ScanWithCatalog("""
            CREATE TABLE dbo.Orders (OrderCode varchar(20) NULL);
            CREATE INDEX IX_Orders_OrderCode ON dbo.Orders(OrderCode);
            GO
            SELECT 1 FROM dbo.Orders WHERE CAST(ISNULL(OrderCode, '') AS nvarchar(20)) = N'A1';
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.CastOrConvertOnColumn, finding.Kind);
        Assert.Equal("OrderCode", finding.ColumnName);
        Assert.Equal("CAST", finding.Detail);
        Assert.True(finding.Indexed);
    }

    [Fact]
    public void CastWrappingExpressionWithNoColumnAtAll_DoesNotFire()
    {
        var findings = ScanWithCatalog("""
            CREATE TABLE dbo.Orders (OrderCode varchar(20) NULL);
            GO
            SELECT 1 FROM dbo.Orders WHERE CAST(ISNULL('literal', '') AS nvarchar(20)) = N'A1';
            """);

        Assert.Empty(findings);
    }
}
