using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class ScalarUdfScannerTests
{
    private static IReadOnlyList<ScalarUdfFinding> ScanSql(string sql, int? compatibilityLevel = null)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        catalog.CompatibilityLevel = compatibilityLevel;
        var (views, _) = ViewDefinitionExtractor.Extract([result], catalog.DefaultCollation, catalog.TypeAliases);
        var scalarUdfMap = ScalarUdfMap.Build(views, catalog);
        return ScalarUdfScanner.Scan(result, catalog, scalarUdfMap);
    }

    [Fact]
    public void ScalarUdfInWhereClause_FiresPredicateInvocation()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_IsActive(@x INT) RETURNS BIT AS BEGIN RETURN 1; END;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            SELECT Id FROM dbo.T WHERE dbo.fn_IsActive(Id) = 1;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(ScalarUdfFindingKind.PredicateInvocation, finding.Kind);
        Assert.Equal(ScalarUdfContext.Where, finding.Context);
        Assert.Equal("dbo.fn_IsActive", finding.FunctionQualifiedName);
    }

    [Fact]
    public void BuiltInFunctionInWhereClause_NeverFires()
    {
        var findings = ScanSql("""
            CREATE TABLE dbo.T (Id INT NOT NULL, Notes VARCHAR(50) NOT NULL);
            GO
            SELECT Id FROM dbo.T WHERE UPPER(Notes) = 'X';
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void UnregisteredTwoPartCall_NeverFires()
    {

        var findings = ScanSql("""
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            SELECT Id FROM dbo.T WHERE dbo.fn_Unknown(Id) = 1;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void ScalarUdfInJoinOn_FiresPredicateInvocationWithJoinOnContext()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_Match(@x INT, @y INT) RETURNS BIT AS BEGIN RETURN 1; END;
            GO
            CREATE TABLE dbo.A (Id INT NOT NULL);
            GO
            CREATE TABLE dbo.B (Id INT NOT NULL);
            GO
            SELECT a.Id FROM dbo.A a JOIN dbo.B b ON dbo.fn_Match(a.Id, b.Id) = 1;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(ScalarUdfFindingKind.PredicateInvocation, finding.Kind);
        Assert.Equal(ScalarUdfContext.JoinOn, finding.Context);
    }

    [Fact]
    public void ScalarUdfInSelectList_FiresProjectionInvocationWithSelectListContext()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_Compute(@x INT) RETURNS INT AS BEGIN RETURN @x + 1; END;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            SELECT Id, dbo.fn_Compute(Id) AS Computed FROM dbo.T;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(ScalarUdfFindingKind.ProjectionInvocation, finding.Kind);
        Assert.Equal(ScalarUdfContext.SelectList, finding.Context);
    }

    [Fact]
    public void ScalarUdfInOrderBy_FiresProjectionInvocationWithOrderByContext()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_Compute(@x INT) RETURNS INT AS BEGIN RETURN @x + 1; END;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            SELECT Id FROM dbo.T ORDER BY dbo.fn_Compute(Id);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(ScalarUdfContext.OrderBy, finding.Context);
    }

    [Fact]
    public void ScalarUdfInGroupBy_FiresProjectionInvocationWithGroupByContext()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_Bucket(@x INT) RETURNS INT AS BEGIN RETURN @x / 10; END;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            SELECT dbo.fn_Bucket(Id) FROM dbo.T GROUP BY dbo.fn_Bucket(Id);
            """);

        Assert.Contains(findings, f => f.Context == ScalarUdfContext.GroupBy);
    }

    [Fact]
    public void ScalarUdfInHaving_FiresPredicateInvocationWithHavingContext()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_Threshold(@x INT) RETURNS BIT AS BEGIN RETURN 1; END;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            SELECT COUNT(*) FROM dbo.T GROUP BY Id HAVING dbo.fn_Threshold(Id) = 1;
            """);

        Assert.Contains(findings, f => f.Kind == ScalarUdfFindingKind.PredicateInvocation && f.Context == ScalarUdfContext.Having);
    }

    [Fact]
    public void ScalarUdfInSetAssignment_FiresProjectionInvocationWithSetAssignmentContext()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_Compute(@x INT) RETURNS INT AS BEGIN RETURN @x + 1; END;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL, Value INT NOT NULL);
            GO
            UPDATE dbo.T SET Value = dbo.fn_Compute(Id);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(ScalarUdfContext.SetAssignment, finding.Context);
    }

    [Fact]
    public void ScalarUdfInVariableAssignment_FiresProjectionInvocationWithVariableAssignmentContext()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_Compute(@x INT) RETURNS INT AS BEGIN RETURN @x + 1; END;
            GO
            DECLARE @v INT;
            SET @v = dbo.fn_Compute(1);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(ScalarUdfContext.VariableAssignment, finding.Context);
    }

    [Fact]
    public void UdfCallNestedInsideAnotherUdfCallArguments_ReportsOutermostOnly()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_Inner(@x INT) RETURNS INT AS BEGIN RETURN @x + 1; END;
            GO
            CREATE FUNCTION dbo.fn_Outer(@x INT) RETURNS INT AS BEGIN RETURN @x * 2; END;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            SELECT dbo.fn_Outer(dbo.fn_Inner(Id)) FROM dbo.T;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.fn_Outer", finding.FunctionQualifiedName);
    }

    [Fact]
    public void NestedUnderView_FiresWithDepthAndOrigin()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_Compute(@x INT) RETURNS INT AS BEGIN RETURN @x + 1; END;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Computed AS SELECT Id, dbo.fn_Compute(Id) AS Computed FROM dbo.T;
            GO
            SELECT Id FROM dbo.vw_Computed;
            """);

        var nested = Assert.Single(findings, f => f.Kind == ScalarUdfFindingKind.NestedUnderViewOrTvf);
        Assert.Equal("dbo.fn_Compute", nested.FunctionQualifiedName);
        Assert.Equal("dbo.vw_Computed", nested.ReferencedObjectQualifiedName);
        Assert.Equal(1, nested.Depth);

        Assert.Contains(findings, f => f.Kind == ScalarUdfFindingKind.ProjectionInvocation);
    }

    [Fact]
    public void CallWithAllLiteralArgumentsOnNonSchemaBoundFunction_FlagsConstantArgumentsNotFolded()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_Compute(@x INT) RETURNS INT AS BEGIN RETURN @x + 1; END;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            SELECT dbo.fn_Compute(5) FROM dbo.T;
            """);

        var finding = Assert.Single(findings);
        Assert.True(finding.ConstantArgumentsNotFolded);
    }

    [Fact]
    public void CallWithAllLiteralArgumentsOnSchemaBoundFunction_DoesNotFlagConstantArgumentsNotFolded()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_Compute(@x INT) RETURNS INT WITH SCHEMABINDING AS BEGIN RETURN @x + 1; END;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            SELECT dbo.fn_Compute(5) FROM dbo.T;
            """);

        var finding = Assert.Single(findings);
        Assert.False(finding.ConstantArgumentsNotFolded);
    }

    [Fact]
    public void FunctionUsingGetDate_ReportsNotInlineableWithBlockerReason()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_Now(@x INT) RETURNS DATETIME AS BEGIN RETURN GETDATE(); END;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            SELECT dbo.fn_Now(Id) FROM dbo.T;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(ScalarUdfInlineability.NotInlineable, finding.Inlineability);
        Assert.Contains("GETDATE", finding.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FunctionUsingGoto_ReportsNotInlineableWithBlockerReason()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_Goto(@x INT) RETURNS INT AS
            BEGIN
                DECLARE @v INT = @x;
                IF @v IS NULL
                BEGIN
                    GOTO DONE;
                END
                SET @v = @v + 1;
                DONE:
                RETURN @v;
            END;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            SELECT dbo.fn_Goto(Id) FROM dbo.T;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(ScalarUdfInlineability.NotInlineable, finding.Inlineability);
        Assert.Contains("GOTO", finding.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FunctionWithSelectAccumulatorAssignment_ReportsNotInlineableWithBlockerReason()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_Accum(@x INT) RETURNS VARCHAR(200) AS
            BEGIN
                DECLARE @s VARCHAR(200) = '';
                SELECT @s = COALESCE(@s + ',', '') + CAST(Val AS VARCHAR(20))
                FROM dbo.Source
                WHERE OwnerId = @x;
                RETURN @s;
            END;
            GO
            CREATE TABLE dbo.Source (OwnerId INT NOT NULL, Val INT NOT NULL);
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            SELECT dbo.fn_Accum(Id) FROM dbo.T;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(ScalarUdfInlineability.NotInlineable, finding.Inlineability);
        Assert.Contains("accumulator", finding.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FunctionWithPlainSelectAssignmentFromTable_DoesNotFlagAccumulatorBlocker()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_PlainSelect(@x INT) RETURNS INT AS
            BEGIN
                DECLARE @v INT;
                SELECT @v = Val FROM dbo.Source WHERE OwnerId = @x;
                RETURN @v;
            END;
            GO
            CREATE TABLE dbo.Source (OwnerId INT NOT NULL, Val INT NOT NULL);
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            SELECT dbo.fn_PlainSelect(Id) FROM dbo.T;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(ScalarUdfInlineability.Unknown, finding.Inlineability);
    }

    [Fact]
    public void CleanFunctionBody_ReportsUnknownInlineabilityNeverInlineable()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_Compute(@x INT) RETURNS INT AS BEGIN RETURN @x + 1; END;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            SELECT dbo.fn_Compute(Id) FROM dbo.T;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(ScalarUdfInlineability.Unknown, finding.Inlineability);
    }

    [Fact]
    public void ClrScalarUdf_AlwaysReportsNotInlineable()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_Clr(@x INT) RETURNS INT EXTERNAL NAME [Asm].[Cls].[Method];
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            SELECT dbo.fn_Clr(Id) FROM dbo.T;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(ScalarUdfKind.Clr, finding.UdfKind);
        Assert.Equal(ScalarUdfInlineability.NotInlineable, finding.Inlineability);
    }

    [Fact]
    public void CleanFunctionBody_AtCompatibilityLevel140_ReportsNotInlineableCompatBlocker()
    {
        var findings = ScanSql(
            """
            CREATE FUNCTION dbo.fn_Compute(@x INT) RETURNS INT AS BEGIN RETURN @x + 1; END;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            SELECT dbo.fn_Compute(Id) FROM dbo.T;
            """,
            compatibilityLevel: 140);

        var finding = Assert.Single(findings);
        Assert.Equal(ScalarUdfInlineability.NotInlineable, finding.Inlineability);
        Assert.Contains("140", finding.InlineabilityBlocker);
    }

    [Fact]
    public void CleanFunctionBody_AtCompatibilityLevel150_ReportsUnknownInlineability()
    {
        var findings = ScanSql(
            """
            CREATE FUNCTION dbo.fn_Compute(@x INT) RETURNS INT AS BEGIN RETURN @x + 1; END;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            SELECT dbo.fn_Compute(Id) FROM dbo.T;
            """,
            compatibilityLevel: 150);

        var finding = Assert.Single(findings);
        Assert.Equal(ScalarUdfInlineability.Unknown, finding.Inlineability);
    }

    [Fact]
    public void FunctionReferencingFiftyTables_ReportsNotInlineableTableLimitBlocker()
    {
        var subqueries = string.Concat(Enumerable.Range(0, 50).Select(_ => " + (SELECT v FROM dbo.Source)"));
        var findings = ScanSql(
            $"""
            CREATE FUNCTION dbo.fn_ManyTables(@x INT) RETURNS INT AS BEGIN RETURN @x{subqueries}; END;
            GO
            CREATE TABLE dbo.Source (v INT NOT NULL);
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            SELECT dbo.fn_ManyTables(Id) FROM dbo.T;
            """,
            compatibilityLevel: 150);

        var finding = Assert.Single(findings, f => f.FunctionQualifiedName == "dbo.fn_ManyTables");
        Assert.Equal(ScalarUdfInlineability.NotInlineable, finding.Inlineability);
        Assert.Contains("50", finding.InlineabilityBlocker!);
        Assert.Contains("49", finding.InlineabilityBlocker!);
    }

    [Fact]
    public void FunctionReferencingFortyNineTables_DoesNotFlagTableLimitBlocker()
    {
        var subqueries = string.Concat(Enumerable.Range(0, 49).Select(_ => " + (SELECT v FROM dbo.Source)"));
        var findings = ScanSql(
            $"""
            CREATE FUNCTION dbo.fn_ManyTables(@x INT) RETURNS INT AS BEGIN RETURN @x{subqueries}; END;
            GO
            CREATE TABLE dbo.Source (v INT NOT NULL);
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            SELECT dbo.fn_ManyTables(Id) FROM dbo.T;
            """,
            compatibilityLevel: 150);

        var finding = Assert.Single(findings, f => f.FunctionQualifiedName == "dbo.fn_ManyTables");
        Assert.Equal(ScalarUdfInlineability.Unknown, finding.Inlineability);
    }
}
