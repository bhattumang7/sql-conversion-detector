using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class CatchAllPredicateScannerTests
{
    private static IReadOnlyList<CatchAllPredicateFinding> Scan(string sql)
    {
        var ddl = "CREATE TABLE dbo.Customers (Id INT NOT NULL, Code VARCHAR(20) NOT NULL, Region VARCHAR(20) NOT NULL, INDEX IX_Customers_Code (Code));";
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return CatchAllPredicateScanner.Scan(result, catalog);
    }

    [Fact]
    public void CatchAllPair_InsideUnsatisfiableAndBranch_EliminatedByNormalization()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE (Code = @p OR @p IS NULL) AND Id = 1 AND Id = 2; END");

        Assert.Empty(findings);
    }

    [Fact]
    public void CatchAllPair_AbsorbedByEquivalentOuterConjunct_EliminatedByNormalization()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE Code = @p AND (Code = @p OR @p IS NULL); END");

        Assert.Empty(findings);
    }

    [Fact]
    public void CanonicalOrder_ColumnEqualsParameterOrParameterIsNull_Fires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE (Code = @p OR @p IS NULL); END");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Customers", finding.TableQualifiedName);
        Assert.Equal("Code", finding.ColumnName);
        Assert.Equal("@p", finding.ParameterName);
        Assert.True(finding.Indexed);
    }

    [Fact]
    public void SwappedOrder_ParameterIsNullOrColumnEqualsParameter_Fires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE (@p IS NULL OR Code = @p); END");

        var finding = Assert.Single(findings);
        Assert.Equal("Code", finding.ColumnName);
    }

    [Fact]
    public void ChainedIndependentClauses_FiresOncePerMatchedPair()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20), @q VARCHAR(20) AS
            BEGIN
                SELECT 1 FROM dbo.Customers WHERE (Code = @p OR @p IS NULL) AND (Region = @q OR @q IS NULL);
            END
            """);

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.ColumnName == "Code" && f.ParameterName == "@p");
        Assert.Contains(findings, f => f.ColumnName == "Region" && f.ParameterName == "@q");
    }

    [Fact]
    public void CteSharesNameWithIndexedBaseTable_AttributesThroughToTheRealUnderlyingColumn()
    {

        var findings = Scan(
            """
            CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS
            BEGIN
                WITH Customers AS (SELECT Region AS Code FROM dbo.Customers)
                SELECT 1 FROM Customers WHERE (Code = @p OR @p IS NULL);
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Customers", finding.TableQualifiedName);
        Assert.Equal("Region", finding.ColumnName);
        Assert.False(finding.Indexed);
    }

    [Fact]
    public void UnindexedColumn_StillFiresButReportsUnindexed()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE (Region = @p OR @p IS NULL); END");

        var finding = Assert.Single(findings);
        Assert.False(finding.Indexed);
    }

    [Fact]
    public void DeclaredLocalVariable_NotAFormalParameter_NeverFires()
    {

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find AS BEGIN DECLARE @p VARCHAR(20) = 'ABC'; SELECT 1 FROM dbo.Customers WHERE (Code = @p OR @p IS NULL); END");

        Assert.Empty(findings);
    }

    [Fact]
    public void DifferentVariablesInEqualityAndIsNull_NeverFires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20), @q VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE (Code = @p OR @q IS NULL); END");

        Assert.Empty(findings);
    }

    [Fact]
    public void WrappedColumn_NeverFires()
    {

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE (UPPER(Code) = @p OR @p IS NULL); END");

        Assert.Empty(findings);
    }

    [Fact]
    public void UnrelatedOr_NeverFires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20), @q VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE (Code = @p OR Region = @q); END");

        Assert.Empty(findings);
    }

    [Fact]
    public void PlainEquality_NoOr_NeverFires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE Code = @p; END");

        Assert.Empty(findings);
    }

    [Fact]
    public void NegatedCatchAllShape_NeverFires()
    {

        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE NOT (Code = @p OR @p IS NULL); END");

        Assert.Empty(findings);
    }

    [Fact]
    public void StatementOptionRecompile_SuppressesTheFinding()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE (Code = @p OR @p IS NULL) OPTION (RECOMPILE); END");

        Assert.Empty(findings);
    }

    [Fact]
    public void ProcedureWithRecompile_SuppressesEveryFindingInTheBody()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) WITH RECOMPILE AS BEGIN SELECT 1 FROM dbo.Customers WHERE (Code = @p OR @p IS NULL); END");

        Assert.Empty(findings);
    }

    [Fact]
    public void UpdateStatement_SameShapeInWhereClause_Fires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN UPDATE dbo.Customers SET Region = 'X' WHERE (Code = @p OR @p IS NULL); END");

        var finding = Assert.Single(findings);
        Assert.Equal("Code", finding.ColumnName);
    }

    [Fact]
    public void AdHocBatchParameter_NoEnclosingProcedure_NeverFires()
    {

        var findings = Scan("DECLARE @p VARCHAR(20) = 'ABC'; SELECT 1 FROM dbo.Customers WHERE (Code = @p OR @p IS NULL);");

        Assert.Empty(findings);
    }

    [Fact]
    public void AlterProcedure_SameShape_Fires()
    {
        var findings = Scan(
            "ALTER PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE (Code = @p OR @p IS NULL); END");

        Assert.Single(findings);
    }

    [Fact]
    public void CreateOrAlterProcedure_SameShape_Fires()
    {
        var findings = Scan(
            "CREATE OR ALTER PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE (Code = @p OR @p IS NULL); END");

        Assert.Single(findings);
    }

    [Fact]
    public void CreateFunction_SameShape_Fires()
    {
        var findings = Scan(
            "CREATE FUNCTION dbo.usf_Find (@p VARCHAR(20)) RETURNS INT AS BEGIN DECLARE @r INT = (SELECT COUNT(*) FROM dbo.Customers WHERE (Code = @p OR @p IS NULL)); RETURN @r; END");

        Assert.Single(findings);
    }

    [Fact]
    public void CreateTrigger_ParametersAlwaysEmpty_NeverFires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_Customers ON dbo.Customers AFTER INSERT AS BEGIN SELECT 1 FROM dbo.Customers WHERE Code = 'x'; END");

        Assert.Empty(findings);
    }

    [Fact]
    public void AlterFunction_SameShape_Fires()
    {
        var findings = Scan(
            "ALTER FUNCTION dbo.usf_Find (@p VARCHAR(20)) RETURNS INT AS BEGIN DECLARE @r INT = (SELECT COUNT(*) FROM dbo.Customers WHERE (Code = @p OR @p IS NULL)); RETURN @r; END");

        Assert.Single(findings);
    }

    [Fact]
    public void CreateOrAlterFunction_SameShape_Fires()
    {
        var findings = Scan(
            "CREATE OR ALTER FUNCTION dbo.usf_Find (@p VARCHAR(20)) RETURNS INT AS BEGIN DECLARE @r INT = (SELECT COUNT(*) FROM dbo.Customers WHERE (Code = @p OR @p IS NULL)); RETURN @r; END");

        Assert.Single(findings);
    }

    [Fact]
    public void AlterTrigger_ParametersAlwaysEmpty_NeverFires()
    {
        var findings = Scan(
            "ALTER TRIGGER dbo.trg_Customers ON dbo.Customers AFTER INSERT AS BEGIN SELECT 1 FROM dbo.Customers WHERE Code = 'x'; END");

        Assert.Empty(findings);
    }

    [Fact]
    public void CreateOrAlterTrigger_ParametersAlwaysEmpty_NeverFires()
    {
        var findings = Scan(
            "CREATE OR ALTER TRIGGER dbo.trg_Customers ON dbo.Customers AFTER INSERT AS BEGIN SELECT 1 FROM dbo.Customers WHERE Code = 'x'; END");

        Assert.Empty(findings);
    }

    [Fact]
    public void DeleteStatement_SameShapeInWhereClause_Fires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN DELETE FROM dbo.Customers WHERE (Code = @p OR @p IS NULL); END");

        var finding = Assert.Single(findings);
        Assert.Equal("Code", finding.ColumnName);
    }

    [Fact]
    public void DeleteStatement_WithRecompileHint_NeverFires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN DELETE FROM dbo.Customers WHERE (Code = @p OR @p IS NULL) OPTION (RECOMPILE); END");

        Assert.Empty(findings);
    }

    [Fact]
    public void MergeStatement_NoStandaloneWhereClause_NeverThrows()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN "
            + "MERGE dbo.Customers AS t USING (SELECT @p AS Code) AS s ON t.Code = s.Code "
            + "WHEN MATCHED THEN UPDATE SET t.Region = 'X' "
            + "WHEN NOT MATCHED THEN INSERT (Id, Code, Region) VALUES (1, s.Code, 'X'); END");

        Assert.Empty(findings);
    }

    [Fact]
    public void OrLeavesWrappedInParentheses_StillDetected()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN SELECT 1 FROM dbo.Customers WHERE (Code = @p) OR (@p IS NULL); END");

        var finding = Assert.Single(findings);
        Assert.Equal("Code", finding.ColumnName);
    }

    [Fact]
    public void ColumnFromCte_NotABaseColumnAtDepthZero_NeverFires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find @p VARCHAR(20) AS BEGIN "
            + "WITH C AS (SELECT Code + '' AS Code FROM dbo.Customers) "
            + "SELECT 1 FROM C WHERE (C.Code = @p OR @p IS NULL); END");

        Assert.Empty(findings);
    }
}
