using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-reference.md Appendix 8 - each clause shape here was independently
/// oracle-confirmed against the standing Docker instance (2026-08-20) by inspecting the real
/// cached PREPARED plan text under PARAMETERIZATION FORCED, not read off the DMV's name alone.
/// Fully syntax-only; the live is_parameterization_forced precondition is gated entirely outside
/// this scanner (see ForcedParameterizationFinding's own doc comment).
/// </summary>
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
        // The ordinal-position idiom (ORDER BY 1) is a structurally different, untested shape -
        // deliberately excluded, see OrderByExpressionLiteral's own doc comment.
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
    public void DoubleColonStaticCallLiteralArgument_Fires()
    {
        var findings = Scan("SELECT geography::Parse('POINT(1 1)').STAsText();");

        Assert.Contains(findings, f => f.Kind == ForcedParameterizationFindingKind.DoubleColonCallArgumentLiteral);
    }

    [Fact]
    public void OrdinaryInstanceMethodCall_NeverFiresDoubleColon()
    {
        var findings = Scan("SELECT dbo.Fn(Id) FROM dbo.T;");

        Assert.DoesNotContain(findings, f => f.Kind == ForcedParameterizationFindingKind.DoubleColonCallArgumentLiteral);
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
    public void CheckSumLiteralArgument_Fires()
    {
        var findings = Scan("SELECT CHECKSUM('Literal');");

        Assert.Contains(findings, f => f.Kind == ForcedParameterizationFindingKind.CheckSumArgumentLiteral);
    }

    [Fact]
    public void CheckSumColumnArgument_NeverFires()
    {
        var findings = Scan("SELECT CHECKSUM(Id) FROM dbo.T;");

        Assert.DoesNotContain(findings, f => f.Kind == ForcedParameterizationFindingKind.CheckSumArgumentLiteral);
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
    public void ModuleQualifiedName_ReflectsEnclosingProcedure()
    {
        var findings = Scan("CREATE PROCEDURE dbo.SearchOrders AS SELECT * FROM dbo.T WHERE Name LIKE 'abc%';");

        var finding = Assert.Single(findings, f => f.Kind == ForcedParameterizationFindingKind.LikePatternLiteral);
        Assert.Equal("dbo.SearchOrders", finding.ModuleQualifiedName);
    }
}
