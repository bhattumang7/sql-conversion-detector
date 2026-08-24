using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

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

    [Fact]
    public void LineLength_ExactlyAtThreshold_NeverFires()
    {
        var sql = $"SELECT {new string('a', 14)} AS Col1;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxLineLength: 30));

        Assert.DoesNotContain(findings, f => f.Kind == CodeMetricFindingKind.LineTooLong);
    }

    [Fact]
    public void LineLength_OneOverThreshold_FiresWithExactMeasuredValue()
    {
        var sql = $"SELECT {new string('a', 15)} AS Col1;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxLineLength: 30));

        var finding = Assert.Single(findings, f => f.Kind == CodeMetricFindingKind.LineTooLong);
        Assert.Equal(31, finding.MeasuredValue);
        Assert.Equal(30, finding.Threshold);
    }

    [Fact]
    public void ModuleLength_ExactlyAtThreshold_NeverFires()
    {
        var sql = string.Join("\n", Enumerable.Repeat("SELECT 1;", 5));
        var findings = Scan(sql, new CodeMetricThresholds(MaxModuleLines: 5));

        Assert.DoesNotContain(findings, f => f.Kind == CodeMetricFindingKind.ModuleTooLong);
    }

    [Fact]
    public void ModuleLength_OneOverThreshold_FiresWithExactMeasuredValue()
    {
        var sql = string.Join("\n", Enumerable.Repeat("SELECT 1;", 6));
        var findings = Scan(sql, new CodeMetricThresholds(MaxModuleLines: 5));

        var finding = Assert.Single(findings, f => f.Kind == CodeMetricFindingKind.ModuleTooLong);
        Assert.Equal(6, finding.MeasuredValue);
    }

    [Fact]
    public void RoutineLength_ExactlyAtThreshold_NeverFires()
    {
        var body = string.Join("\n", Enumerable.Repeat("SELECT 1;", 3));
        var sql = $"CREATE PROCEDURE dbo.EdgeProc AS BEGIN\n{body}\nEND;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxRoutineLines: 5));

        Assert.DoesNotContain(findings, f => f.Kind == CodeMetricFindingKind.RoutineTooLong);
    }

    [Fact]
    public void RoutineLength_OneOverThreshold_FiresWithExactMeasuredValue()
    {
        var body = string.Join("\n", Enumerable.Repeat("SELECT 1;", 4));
        var sql = $"CREATE PROCEDURE dbo.EdgeProc AS BEGIN\n{body}\nEND;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxRoutineLines: 5));

        var finding = Assert.Single(findings, f => f.Kind == CodeMetricFindingKind.RoutineTooLong);
        Assert.Equal(6, finding.MeasuredValue);
    }

    [Fact]
    public void TooManyParameters_ExactlyAtThreshold_NeverFires()
    {
        var sql = "CREATE PROCEDURE dbo.EdgeParams (@A int, @B int) AS BEGIN SELECT 1; END;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxParameters: 2));

        Assert.DoesNotContain(findings, f => f.Kind == CodeMetricFindingKind.TooManyParameters);
    }

    [Fact]
    public void AlterProcedureStatement_IsAlsoAnalyzed()
    {
        var sql = "ALTER PROCEDURE dbo.ManyParams (@A int, @B int, @C int) AS BEGIN SELECT 1; END;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxParameters: 2));

        var finding = Assert.Single(findings, f => f.Kind == CodeMetricFindingKind.TooManyParameters);
        Assert.Equal(3, finding.MeasuredValue);
        Assert.Equal("procedure", finding.DetailText);
    }

    [Fact]
    public void AlterFunctionStatement_IsAlsoAnalyzed()
    {
        var sql = "ALTER FUNCTION dbo.ManyParams (@A int, @B int, @C int) RETURNS int AS BEGIN RETURN 1; END;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxParameters: 2));

        var finding = Assert.Single(findings, f => f.Kind == CodeMetricFindingKind.TooManyParameters);
        Assert.Equal("function", finding.DetailText);
    }

    [Fact]
    public void CreateTriggerStatement_RoutineTooLong_Fires()
    {
        var body = string.Join("\n", Enumerable.Repeat("SELECT 1;", 6));
        var sql = $"CREATE TRIGGER dbo.BigTrigger ON dbo.SomeTable AFTER INSERT AS BEGIN\n{body}\nEND;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxRoutineLines: 5));

        var finding = Assert.Single(findings, f => f.Kind == CodeMetricFindingKind.RoutineTooLong);
        Assert.Equal("dbo.BigTrigger", finding.ModuleQualifiedName);
        Assert.Equal("trigger", finding.DetailText);
    }

    [Fact]
    public void AlterTriggerStatement_RoutineTooLong_Fires()
    {
        var body = string.Join("\n", Enumerable.Repeat("SELECT 1;", 6));
        var sql = $"ALTER TRIGGER dbo.BigTrigger ON dbo.SomeTable AFTER INSERT AS BEGIN\n{body}\nEND;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxRoutineLines: 5));

        var finding = Assert.Single(findings, f => f.Kind == CodeMetricFindingKind.RoutineTooLong);
        Assert.Equal("trigger", finding.DetailText);
    }

    [Fact]
    public void UnqualifiedProcedureName_HasNoSchemaPrefix()
    {
        var sql = "CREATE PROCEDURE UnqualifiedProc (@A int, @B int, @C int) AS BEGIN SELECT 1; END;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxParameters: 2));

        var finding = Assert.Single(findings, f => f.Kind == CodeMetricFindingKind.TooManyParameters);
        Assert.Equal("UnqualifiedProc", finding.ModuleQualifiedName);
    }

    [Fact]
    public void NestingDepth_ExactlyAtThreshold_NeverFires()
    {
        var sql = """
            CREATE PROCEDURE dbo.TwoDeep AS
            BEGIN
                IF 1 = 1
                BEGIN
                    IF 2 = 2
                    BEGIN
                        SELECT 1;
                    END
                END
            END;
            """;
        var findings = Scan(sql, new CodeMetricThresholds(MaxNestingDepth: 2));

        Assert.DoesNotContain(findings, f => f.Kind == CodeMetricFindingKind.NestingTooDeep);
    }

    [Fact]
    public void NestingTooDeep_FiresOnlyOnceEvenWhenNestingGoesDeeper()
    {
        var sql = """
            CREATE PROCEDURE dbo.VeryDeepNest AS
            BEGIN
                IF 1 = 1
                BEGIN
                    IF 2 = 2
                    BEGIN
                        IF 3 = 3
                        BEGIN
                            IF 4 = 4
                            BEGIN
                                IF 5 = 5
                                BEGIN
                                    SELECT 1;
                                END
                            END
                        END
                    END
                END
            END;
            """;
        var findings = Scan(sql, new CodeMetricThresholds(MaxNestingDepth: 2));

        var finding = Assert.Single(findings, f => f.Kind == CodeMetricFindingKind.NestingTooDeep);
        Assert.Equal(3, finding.MeasuredValue);
    }

    [Fact]
    public void TryCatchStatement_CountsTowardNestingDepth()
    {
        var sql = """
            CREATE PROCEDURE dbo.TryNest AS
            BEGIN
                IF 1 = 1
                BEGIN
                    BEGIN TRY
                        SELECT 1;
                    END TRY
                    BEGIN CATCH
                        SELECT 2;
                    END CATCH
                END
            END;
            """;
        var findings = Scan(sql, new CodeMetricThresholds(MaxNestingDepth: 1));

        Assert.Contains(findings, f => f.Kind == CodeMetricFindingKind.NestingTooDeep);
    }

    [Fact]
    public void WhileStatement_TooManyConditionalOperators_Fires()
    {
        var sql = "WHILE @A = 1 AND @B = 2 AND @C = 3 SELECT 1;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxConditionalOperators: 1));

        var finding = Assert.Single(findings, f => f.Kind == CodeMetricFindingKind.TooManyConditionalOperators);
        Assert.Equal(2, finding.MeasuredValue);
    }

    [Fact]
    public void ConditionalOperators_ExactlyAtThreshold_NeverFires()
    {
        var sql = "IF @A = 1 AND @B = 2 AND @C = 3 SELECT 1;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxConditionalOperators: 2));

        Assert.DoesNotContain(findings, f => f.Kind == CodeMetricFindingKind.TooManyConditionalOperators);
    }

    [Fact]
    public void ParenthesizedPredicate_DoesNotInflateConditionalOperatorCount()
    {
        var sql = "IF (@A = 1 AND @B = 2) AND @C = 3 SELECT 1;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxConditionalOperators: 1));

        var finding = Assert.Single(findings, f => f.Kind == CodeMetricFindingKind.TooManyConditionalOperators);
        Assert.Equal(2, finding.MeasuredValue);
    }

    [Fact]
    public void NotExpression_DoesNotCountAsConditionalOperator()
    {
        var sql = "IF NOT (@A = 1 AND @B = 2) SELECT 1;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxConditionalOperators: 0));

        var finding = Assert.Single(findings, f => f.Kind == CodeMetricFindingKind.TooManyConditionalOperators);
        Assert.Equal(1, finding.MeasuredValue);
    }

    [Fact]
    public void CaseBranches_ExactlyAtThreshold_NeverFires()
    {
        var sql = "SELECT CASE @A WHEN 1 THEN 'a' WHEN 2 THEN 'b' END;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxCaseBranches: 2));

        Assert.DoesNotContain(findings, f => f.Kind == CodeMetricFindingKind.TooManyCaseBranches);
    }

    [Fact]
    public void SearchedCaseExpression_TooManyCaseBranches_Fires()
    {
        var sql = "SELECT CASE WHEN @A = 1 THEN 'a' WHEN @A = 2 THEN 'b' WHEN @A = 3 THEN 'c' END;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxCaseBranches: 2));

        var finding = Assert.Single(findings, f => f.Kind == CodeMetricFindingKind.TooManyCaseBranches);
        Assert.Equal(3, finding.MeasuredValue);
    }

    [Fact]
    public void SearchedCaseExpression_CaseBranchTooLong_Fires()
    {
        var sql = """
            SELECT CASE
                WHEN @A = 1 THEN
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
    public void CaseBranchLines_ExactlyAtThreshold_NeverFires()
    {
        var sql = """
            SELECT CASE @A
                WHEN 1 THEN
                    'a'
                    +
                    'b'
                ELSE 'z'
            END;
            """;
        var findings = Scan(sql, new CodeMetricThresholds(MaxCaseBranchLines: 3));

        Assert.DoesNotContain(findings, f => f.Kind == CodeMetricFindingKind.CaseBranchTooLong);
    }

    [Fact]
    public void MultipleFindingKinds_AreOrderedByKindThenLine()
    {
        var sql = string.Join("\n", Enumerable.Repeat("SELECT 1;", 6))
            + "\n" + $"SELECT {new string('a', 60)} AS Col1;";
        var findings = Scan(sql, new CodeMetricThresholds(MaxModuleLines: 5, MaxLineLength: 40));

        Assert.Equal(2, findings.Count);
        Assert.Equal(CodeMetricFindingKind.LineTooLong, findings[0].Kind);
        Assert.Equal(CodeMetricFindingKind.ModuleTooLong, findings[1].Kind);
    }
}
