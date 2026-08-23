using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class ParameterReassignmentPredicateScannerTests
{
    private static IReadOnlyList<ParameterReassignmentPredicateFinding> Scan(string sql)
    {
        var ddl = "CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL, Region VARCHAR(20) NOT NULL, INDEX IX_Customers_Code (Code));";
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return ParameterReassignmentPredicateScanner.Scan(result, catalog);
    }

    [Fact]
    public void SetReassignsParameter_ThenPredicateUse_Fires()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS
            BEGIN
                SET @p = 'OVERWRITTEN';
                SELECT 1 FROM dbo.Customers WHERE Code = @p;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Customers", finding.TableQualifiedName);
        Assert.Equal("Code", finding.ColumnName);
        Assert.Equal("@p", finding.ParameterName);
        Assert.Equal("=", finding.Operator);
        Assert.True(finding.Indexed);
    }

    [Fact]
    public void CteSharesNameWithIndexedBaseTable_AttributesThroughToTheRealUnderlyingColumn()
    {

        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS
            BEGIN
                SET @p = 'OVERWRITTEN';
                WITH Customers AS (SELECT Region AS Code FROM dbo.Customers)
                SELECT 1 FROM Customers WHERE Code = @p;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Customers", finding.TableQualifiedName);
        Assert.Equal("Region", finding.ColumnName);
        Assert.False(finding.Indexed);
    }

    [Fact]
    public void SelectSetVariableReassignsParameter_ThenPredicateUse_Fires()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS
            BEGIN
                SELECT @p = 'OVERWRITTEN';
                SELECT 1 FROM dbo.Customers WHERE Code = @p;
            END
            """);

        Assert.Single(findings);
    }

    [Fact]
    public void PredicateUse_BeforeReassignment_NeverFires()
    {

        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS
            BEGIN
                SELECT 1 FROM dbo.Customers WHERE Code = @p;
                SET @p = 'OVERWRITTEN';
            END
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void NoReassignment_NeverFires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE Code = @p; END");

        Assert.Empty(findings);
    }

    [Fact]
    public void DeclaredLocalVariable_NotAFormalParameter_NeverFires()
    {

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find AS BEGIN DECLARE @p VARCHAR(20) = 'A'; SET @p = 'B'; SELECT 1 FROM dbo.Customers WHERE Code = @p; END");

        Assert.Empty(findings);
    }

    [Fact]
    public void DeclaredLocalVariable_ReassignedThenUsedInPredicate_NeverFires()
    {

        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS
            BEGIN
                DECLARE @local VARCHAR(20);
                SET @local = 'REASSIGNED';
                SELECT 1 FROM dbo.Customers WHERE Code = @local;
            END
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void ReassignmentOnlyInOneIfBranch_MergedAfter_NeverFires()
    {

        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20), @flag INT AS
            BEGIN
                IF @flag = 1
                    SET @p = 'OVERWRITTEN';
                SELECT 1 FROM dbo.Customers WHERE Code = @p;
            END
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void ReassignmentInBothIfAndElseBranches_MergedAfter_Fires()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20), @flag INT AS
            BEGIN
                IF @flag = 1
                    SET @p = 'A';
                ELSE
                    SET @p = 'B';
                SELECT 1 FROM dbo.Customers WHERE Code = @p;
            END
            """);

        Assert.Single(findings);
    }

    [Fact]
    public void ReassignmentInsideSameIfBranch_AsThePredicate_Fires()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20), @flag INT AS
            BEGIN
                IF @flag = 1
                BEGIN
                    SET @p = 'OVERWRITTEN';
                    SELECT 1 FROM dbo.Customers WHERE Code = @p;
                END
            END
            """);

        Assert.Single(findings);
    }

    [Fact]
    public void ReassignmentInsideWhileLoopBody_NeverPropagatesPastTheLoop()
    {

        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS
            BEGIN
                WHILE 1 = 0
                    SET @p = 'OVERWRITTEN';
                SELECT 1 FROM dbo.Customers WHERE Code = @p;
            END
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void ReassignmentInTryBlock_CatchStartsFromPreTryState_NeverInheritsIt()
    {

        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS
            BEGIN
                BEGIN TRY
                    SET @p = 'OVERWRITTEN';
                END TRY
                BEGIN CATCH
                    SELECT 1 FROM dbo.Customers WHERE Code = @p;
                END CATCH
            END
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void OptionRecompileOnStatement_Suppresses()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS
            BEGIN
                SET @p = 'OVERWRITTEN';
                SELECT 1 FROM dbo.Customers WHERE Code = @p OPTION (RECOMPILE);
            END
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void ProcedureWithRecompile_Suppresses()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) WITH RECOMPILE AS
            BEGIN
                SET @p = 'OVERWRITTEN';
                SELECT 1 FROM dbo.Customers WHERE Code = @p;
            END
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void UnindexedColumn_StillFiresButReportsUnindexed()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS
            BEGIN
                SET @p = 'OVERWRITTEN';
                SELECT 1 FROM dbo.Customers WHERE Region = @p;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.False(finding.Indexed);
    }

    [Fact]
    public void UpdateStatement_PredicateSiteRecognized()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS
            BEGIN
                SET @p = 'OVERWRITTEN';
                UPDATE dbo.Customers SET Region = 'X' WHERE Code = @p;
            END
            """);

        Assert.Single(findings);
    }

    [Fact]
    public void RangeOperator_AlsoFires()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS
            BEGIN
                SET @p = 'OVERWRITTEN';
                SELECT 1 FROM dbo.Customers WHERE Code > @p;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(">", finding.Operator);
    }

    [Fact]
    public void GoToAnywhereInProcedure_DeclinesTheWholeProcedure()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS
            BEGIN
                SET @p = 'OVERWRITTEN';
                GOTO Done;
                Done:
                SELECT 1 FROM dbo.Customers WHERE Code = @p;
            END
            """);

        Assert.Empty(findings);
    }
}
