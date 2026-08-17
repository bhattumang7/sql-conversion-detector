using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 4 "Size and complexity metrics" - eight configurable-threshold
/// structural metrics over the AST. Fully syntax-only, no oracle needed - none of these change a
/// query's result or its plan. Tests use small custom thresholds (rather than the real calibrated
/// defaults) so fixtures stay short and readable.
/// </summary>
public sealed class CodeMetricScannerTests
{
    private static IReadOnlyList<CodeMetricFinding> Scan(string sql, CodeMetricThresholds thresholds)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return CodeMetricScanner.Scan(result, thresholds);
    }

    [Fact]
    public void LineTooLong_Fires()
    {
        var sql = $"SELECT {new string('a', 60)} AS Col1;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxLineLength: 40));

        var finding = Assert.Single(findings, f => f.Kind == CodeMetricFindingKind.LineTooLong);
        Assert.Equal(1, finding.Line);
    }

    [Fact]
    public void LineWithinLimit_NeverFires()
    {
        var findings = Scan("SELECT 1;", new CodeMetricThresholds(MaxLineLength: 40));

        Assert.DoesNotContain(findings, f => f.Kind == CodeMetricFindingKind.LineTooLong);
    }

    [Fact]
    public void ModuleTooLong_Fires()
    {
        var sql = string.Join("\n", Enumerable.Repeat("SELECT 1;", 10));
        var findings = Scan(sql, new CodeMetricThresholds(MaxModuleLines: 5));

        Assert.Contains(findings, f => f.Kind == CodeMetricFindingKind.ModuleTooLong);
    }

    [Fact]
    public void ModuleWithinLimit_NeverFires()
    {
        var sql = string.Join("\n", Enumerable.Repeat("SELECT 1;", 3));
        var findings = Scan(sql, new CodeMetricThresholds(MaxModuleLines: 5));

        Assert.DoesNotContain(findings, f => f.Kind == CodeMetricFindingKind.ModuleTooLong);
    }

    [Fact]
    public void RoutineTooLong_Fires()
    {
        var body = string.Join("\n", Enumerable.Repeat("SELECT 1;", 10));
        var sql = $"CREATE PROCEDURE dbo.LongProc AS BEGIN\n{body}\nEND;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxRoutineLines: 5));

        var finding = Assert.Single(findings, f => f.Kind == CodeMetricFindingKind.RoutineTooLong);
        Assert.Equal("dbo.LongProc", finding.ModuleQualifiedName);
    }

    [Fact]
    public void RoutineWithinLimit_NeverFires()
    {
        var sql = "CREATE PROCEDURE dbo.ShortProc AS BEGIN SELECT 1; END;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxRoutineLines: 50));

        Assert.DoesNotContain(findings, f => f.Kind == CodeMetricFindingKind.RoutineTooLong);
    }

    [Fact]
    public void TooManyParameters_Fires()
    {
        var sql = "CREATE PROCEDURE dbo.ManyParams (@A int, @B int, @C int, @D int) AS BEGIN SELECT 1; END;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxParameters: 2));

        var finding = Assert.Single(findings, f => f.Kind == CodeMetricFindingKind.TooManyParameters);
        Assert.Equal(4, finding.MeasuredValue);
    }

    [Fact]
    public void FewParameters_NeverFires()
    {
        var sql = "CREATE PROCEDURE dbo.FewParams (@A int) AS BEGIN SELECT 1; END;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxParameters: 2));

        Assert.DoesNotContain(findings, f => f.Kind == CodeMetricFindingKind.TooManyParameters);
    }

    [Fact]
    public void NestingTooDeep_Fires()
    {
        var sql = """
            CREATE PROCEDURE dbo.DeepNest AS
            BEGIN
                IF 1 = 1
                BEGIN
                    IF 2 = 2
                    BEGIN
                        IF 3 = 3
                        BEGIN
                            SELECT 1;
                        END
                    END
                END
            END;
            """;
        var findings = Scan(sql, new CodeMetricThresholds(MaxNestingDepth: 2));

        Assert.Contains(findings, f => f.Kind == CodeMetricFindingKind.NestingTooDeep);
    }

    [Fact]
    public void ShallowNesting_NeverFires()
    {
        var sql = "CREATE PROCEDURE dbo.Shallow AS BEGIN IF 1 = 1 BEGIN SELECT 1; END END;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxNestingDepth: 4));

        Assert.DoesNotContain(findings, f => f.Kind == CodeMetricFindingKind.NestingTooDeep);
    }

    [Fact]
    public void TooManyConditionalOperators_Fires()
    {
        var sql = "IF @A = 1 AND @B = 2 AND @C = 3 AND @D = 4 SELECT 1;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxConditionalOperators: 2));

        Assert.Contains(findings, f => f.Kind == CodeMetricFindingKind.TooManyConditionalOperators);
    }

    [Fact]
    public void FewConditionalOperators_NeverFires()
    {
        var sql = "IF @A = 1 AND @B = 2 SELECT 1;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxConditionalOperators: 3));

        Assert.DoesNotContain(findings, f => f.Kind == CodeMetricFindingKind.TooManyConditionalOperators);
    }

    [Fact]
    public void TooManyCaseBranches_Fires()
    {
        var sql = "SELECT CASE @A WHEN 1 THEN 'a' WHEN 2 THEN 'b' WHEN 3 THEN 'c' WHEN 4 THEN 'd' END;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxCaseBranches: 2));

        var finding = Assert.Single(findings, f => f.Kind == CodeMetricFindingKind.TooManyCaseBranches);
        Assert.Equal(4, finding.MeasuredValue);
    }

    [Fact]
    public void FewCaseBranches_NeverFires()
    {
        var sql = "SELECT CASE @A WHEN 1 THEN 'a' WHEN 2 THEN 'b' END;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxCaseBranches: 5));

        Assert.DoesNotContain(findings, f => f.Kind == CodeMetricFindingKind.TooManyCaseBranches);
    }

    [Fact]
    public void CaseBranchTooLong_Fires()
    {
        var sql = """
            SELECT CASE @A
                WHEN 1 THEN
                    'a'
                    +
                    'b'
                    +
                    'c'
                ELSE 'z'
            END;
            """;
        var findings = Scan(sql, new CodeMetricThresholds(MaxCaseBranchLines: 2));

        Assert.Contains(findings, f => f.Kind == CodeMetricFindingKind.CaseBranchTooLong);
    }

    [Fact]
    public void CaseBranchWithinLimit_NeverFires()
    {
        var sql = "SELECT CASE @A WHEN 1 THEN 'a' ELSE 'z' END;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxCaseBranchLines: 5));

        Assert.DoesNotContain(findings, f => f.Kind == CodeMetricFindingKind.CaseBranchTooLong);
    }

    [Fact]
    public void CreateFunction_AlsoAnalyzed()
    {
        var sql = "CREATE FUNCTION dbo.ManyParams (@A int, @B int, @C int) RETURNS int AS BEGIN RETURN 1; END;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxParameters: 1));

        Assert.Contains(findings, f => f.Kind == CodeMetricFindingKind.TooManyParameters && f.DetailText == "function");
    }

    [Fact]
    public void AllDefaultThresholds_NeverFireOnASmallFixture()
    {
        var findings = Scan("CREATE PROCEDURE dbo.Trivial (@Id int) AS BEGIN SELECT 1 WHERE @Id = 1; END;", CodeMetricThresholds.Default);

        Assert.Empty(findings);
    }
}
