using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class ForcedParameterizationScannerTests
{
    private static IReadOnlyList<ForcedParameterizationFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return ForcedParameterizationScanner.Scan([result]);
    }

    [Fact]
    public void LikePatternLiteral_Fires()
    {
        var findings = Scan("SELECT * FROM dbo.T WHERE Name LIKE 'abc%';");

        Assert.Contains(findings, f => f.Kind == ForcedParameterizationFindingKind.LikePatternLiteral);
    }

    [Fact]
    public void LikePatternVariable_NeverFires()
    {
        var findings = Scan("CREATE PROCEDURE dbo.P @Pattern varchar(50) AS SELECT * FROM dbo.T WHERE Name LIKE @Pattern;");

        Assert.DoesNotContain(findings, f => f.Kind == ForcedParameterizationFindingKind.LikePatternLiteral);
    }

    [Fact]
    public void TopLiteral_Fires()
    {
        var findings = Scan("SELECT TOP 5 Id FROM dbo.T;");

        Assert.Contains(findings, f => f.Kind == ForcedParameterizationFindingKind.TopOrPagingLiteral);
    }

    [Fact]
    public void TopVariable_NeverFires()
    {
        var findings = Scan("CREATE PROCEDURE dbo.P @N int AS SELECT TOP (@N) Id FROM dbo.T;");

        Assert.DoesNotContain(findings, f => f.Kind == ForcedParameterizationFindingKind.TopOrPagingLiteral);
    }

    [Fact]
    public void OffsetFetchLiteral_Fires()
    {
        var findings = Scan("SELECT Id FROM dbo.T ORDER BY Id OFFSET 5 ROWS FETCH NEXT 3 ROWS ONLY;");

        Assert.Contains(findings, f => f.Kind == ForcedParameterizationFindingKind.TopOrPagingLiteral);
    }

    [Fact]
    public void SelectListLiteral_Fires()
    {
        var findings = Scan("SELECT 'Tag', Id FROM dbo.T;");

        Assert.Contains(findings, f => f.Kind == ForcedParameterizationFindingKind.SelectListLiteral);
    }

    [Fact]
    public void SelectListColumnOnly_NeverFires()
    {
        var findings = Scan("SELECT Id, Name FROM dbo.T;");

        Assert.DoesNotContain(findings, f => f.Kind == ForcedParameterizationFindingKind.SelectListLiteral);
    }

    [Fact]
    public void HavingLiteral_Fires()
    {
        var findings = Scan("SELECT Id, COUNT(*) FROM dbo.T GROUP BY Id HAVING COUNT(*) > 2;");

        Assert.Contains(findings, f => f.Kind == ForcedParameterizationFindingKind.HavingLiteral);
    }

    [Fact]
    public void HavingColumnComparison_NeverFires()
    {
        var findings = Scan("SELECT Id, COUNT(*) AS Cnt FROM dbo.T GROUP BY Id HAVING COUNT(*) > MIN(Id);");

        Assert.DoesNotContain(findings, f => f.Kind == ForcedParameterizationFindingKind.HavingLiteral);
    }

    [Fact]
    public void OrderByCompoundExpressionLiteral_Fires()
    {
        var findings = Scan("SELECT Id FROM dbo.T ORDER BY (Id + 100);");

        Assert.Contains(findings, f => f.Kind == ForcedParameterizationFindingKind.OrderByExpressionLiteral);
    }

    [Fact]
    public void OrderByBareOrdinalLiteral_NeverFires()
    {

        var findings = Scan("SELECT Id FROM dbo.T ORDER BY 1;");

        Assert.DoesNotContain(findings, f => f.Kind == ForcedParameterizationFindingKind.OrderByExpressionLiteral);
    }

    [Fact]
    public void OrderByPlainColumn_NeverFires()
    {
        var findings = Scan("SELECT Id FROM dbo.T ORDER BY Id;");

        Assert.DoesNotContain(findings, f => f.Kind == ForcedParameterizationFindingKind.OrderByExpressionLiteral);
    }

    [Fact]
    public void DoubleColonStaticCallLiteralArgument_NeverFires()
    {
        var findings = Scan("SELECT geography::Parse('POINT(1 1)').STAsText();");

        Assert.Empty(findings);
    }

    [Fact]
    public void TableSampleLiteral_Fires()
    {
        var findings = Scan("SELECT Id FROM dbo.T TABLESAMPLE (10 PERCENT);");

        Assert.Contains(findings, f => f.Kind == ForcedParameterizationFindingKind.TableSampleSizeLiteral);
    }

    [Fact]
    public void NoTableSample_NeverFires()
    {
        var findings = Scan("SELECT Id FROM dbo.T;");

        Assert.DoesNotContain(findings, f => f.Kind == ForcedParameterizationFindingKind.TableSampleSizeLiteral);
    }

    [Fact]
    public void DmlOutputListLiteral_Fires()
    {
        var findings = Scan("INSERT INTO dbo.T (Id) OUTPUT inserted.Id, 'Tag' VALUES (1);");

        Assert.Contains(findings, f => f.Kind == ForcedParameterizationFindingKind.DmlOutputListLiteral);
    }

    [Fact]
    public void DmlOutputColumnsOnly_NeverFires()
    {
        var findings = Scan("INSERT INTO dbo.T (Id) OUTPUT inserted.Id VALUES (1);");

        Assert.DoesNotContain(findings, f => f.Kind == ForcedParameterizationFindingKind.DmlOutputListLiteral);
    }

    [Fact]
    public void ConvertStyleCodeLiteral_Fires()
    {
        var findings = Scan("SELECT CONVERT(varchar, GETDATE(), 101);");

        Assert.Contains(findings, f => f.Kind == ForcedParameterizationFindingKind.ConvertStyleCodeLiteral);
    }

    [Fact]
    public void ConvertWithNoStyle_NeverFires()
    {
        var findings = Scan("SELECT CONVERT(varchar, GETDATE());");

        Assert.DoesNotContain(findings, f => f.Kind == ForcedParameterizationFindingKind.ConvertStyleCodeLiteral);
    }

    [Fact]
    public void CheckSumLiteralArgument_NeverFires()
    {
        var findings = Scan("SELECT CHECKSUM('Literal');");

        Assert.Empty(findings);
    }

    [Fact]
    public void ConstantFoldableExpressionLiteral_Fires()
    {
        var findings = Scan("SELECT Id FROM dbo.T WHERE Id = 1 + 1008;");

        var finding = Assert.Single(findings, f => f.Kind == ForcedParameterizationFindingKind.ConstantFoldableExpressionLiteral);
        Assert.Equal(FindingConfidence.Low, finding.Confidence);
    }

    [Fact]
    public void PlainEqualityLiteral_NeverFiresConstantFoldable()
    {
        var findings = Scan("SELECT Id FROM dbo.T WHERE Id = 1009;");

        Assert.DoesNotContain(findings, f => f.Kind == ForcedParameterizationFindingKind.ConstantFoldableExpressionLiteral);
    }

    [Fact]
    public void GroupByCompoundExpressionLiteral_Fires()
    {
        var findings = Scan("SELECT Id + 1, COUNT(*) FROM dbo.T WHERE Id > 5 GROUP BY (Id + 1);");

        Assert.Contains(findings, f => f.Kind == ForcedParameterizationFindingKind.GroupByExpressionLiteral);
    }

    [Fact]
    public void GroupByPlainColumn_NeverFires()
    {
        var findings = Scan("SELECT Id, COUNT(*) FROM dbo.T GROUP BY Id;");

        Assert.DoesNotContain(findings, f => f.Kind == ForcedParameterizationFindingKind.GroupByExpressionLiteral);
    }

    [Fact]
    public void ModuleQualifiedName_ReflectsEnclosingProcedure()
    {
        var findings = Scan("CREATE PROCEDURE dbo.SearchOrders AS SELECT * FROM dbo.T WHERE Name LIKE 'abc%';");

        var finding = Assert.Single(findings, f => f.Kind == ForcedParameterizationFindingKind.LikePatternLiteral);
        Assert.Equal("dbo.SearchOrders", finding.ModuleQualifiedName);
    }

    [Fact]
    public void ModuleQualifiedName_ReflectsEnclosingAlteredProcedure()
    {
        var findings = Scan("ALTER PROCEDURE dbo.SearchOrders AS SELECT * FROM dbo.T WHERE Name LIKE 'abc%';");

        var finding = Assert.Single(findings, f => f.Kind == ForcedParameterizationFindingKind.LikePatternLiteral);
        Assert.Equal("dbo.SearchOrders", finding.ModuleQualifiedName);
    }

    [Fact]
    public void ModuleQualifiedName_ReflectsEnclosingFunction()
    {
        var findings = Scan(
            "CREATE FUNCTION dbo.SearchOrders() RETURNS TABLE AS RETURN (SELECT * FROM dbo.T WHERE Name LIKE 'abc%');");

        var finding = Assert.Single(findings, f => f.Kind == ForcedParameterizationFindingKind.LikePatternLiteral);
        Assert.Equal("dbo.SearchOrders", finding.ModuleQualifiedName);
    }

    [Fact]
    public void ModuleQualifiedName_ReflectsEnclosingAlteredFunction()
    {
        var findings = Scan(
            "ALTER FUNCTION dbo.SearchOrders() RETURNS TABLE AS RETURN (SELECT * FROM dbo.T WHERE Name LIKE 'abc%');");

        var finding = Assert.Single(findings, f => f.Kind == ForcedParameterizationFindingKind.LikePatternLiteral);
        Assert.Equal("dbo.SearchOrders", finding.ModuleQualifiedName);
    }

    [Fact]
    public void ModuleQualifiedName_ReflectsEnclosingTrigger()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_Orders ON dbo.T AFTER INSERT AS SELECT * FROM dbo.T WHERE Name LIKE 'abc%';");

        var finding = Assert.Single(findings, f => f.Kind == ForcedParameterizationFindingKind.LikePatternLiteral);
        Assert.Equal("dbo.trg_Orders", finding.ModuleQualifiedName);
    }

    [Fact]
    public void ModuleQualifiedName_ReflectsEnclosingAlteredTrigger()
    {
        var findings = Scan(
            "ALTER TRIGGER dbo.trg_Orders ON dbo.T AFTER INSERT AS SELECT * FROM dbo.T WHERE Name LIKE 'abc%';");

        var finding = Assert.Single(findings, f => f.Kind == ForcedParameterizationFindingKind.LikePatternLiteral);
        Assert.Equal("dbo.trg_Orders", finding.ModuleQualifiedName);
    }

    [Fact]
    public void HavingLiteral_ThroughAndedAndParenthesizedComparisons_FindsBothLiterals()
    {
        var findings = Scan(
            "SELECT Id, COUNT(*) FROM dbo.T GROUP BY Id HAVING (COUNT(*) > 2) AND (MIN(Id) < 100);");

        var havingFindings = findings.Where(f => f.Kind == ForcedParameterizationFindingKind.HavingLiteral).ToList();
        Assert.Equal(2, havingFindings.Count);
    }
}
