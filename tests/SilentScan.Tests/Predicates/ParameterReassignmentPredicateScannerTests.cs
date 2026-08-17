using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "Catch-all / kitchen-sink predicates" sibling: "parameter
/// overwritten before use in a predicate" (sniffing-defeat). Structural/path-sensitivity tests
/// for the reachability-walk logic; the general staleness mechanism is oracle-confirmed
/// separately in <see cref="ParameterReassignmentPredicateOracleTests"/>.
/// </summary>
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
        // The sniffed value is still live for THIS predicate - reassignment happens afterward,
        // which is irrelevant to a comparison that already ran against the real sniffed value.
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
        // A DECLARE'd local was never sniffable to begin with - LocalVariablePredicateFinding's
        // own, separate concern, not this stream's.
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find AS BEGIN DECLARE @p VARCHAR(20) = 'A'; SET @p = 'B'; SELECT 1 FROM dbo.Customers WHERE Code = @p; END");

        Assert.Empty(findings);
    }

    [Fact]
    public void DeclaredLocalVariable_ReassignedThenUsedInPredicate_NeverFires()
    {
        // Regression: caught against the real corpus - a DECLARE'd local reassigned via SET
        // then compared in a predicate was incorrectly tracked identically to a genuine formal
        // parameter before this scanner filtered Reassign() to formal-parameter names only. A
        // DECLARE'd local was never sniffed to begin with, so there is no staleness to report -
        // this shape belongs to LocalVariablePredicateFinding, never this stream.
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
        // Sound, not merely conservative: a predicate after the IF cannot be guaranteed to see
        // the reassigned value unless BOTH branches produced it.
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
        // The loop might run zero times - a predicate AFTER the loop cannot be guaranteed to see
        // a reassignment that only happened inside the loop body.
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
        // CATCH enters with the state as of the TRY/CATCH construct's own start (an error can
        // occur at the TRY block's very first statement) - matches OutputParameterScanner's
        // identical documented reasoning.
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
