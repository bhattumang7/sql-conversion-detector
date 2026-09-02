using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class WriteLossExtractionTests
{
    private static IReadOnlyList<WriteLossFinding> Extract(params string[] batches) =>
        ExtractAll(batches).WriteLossFindings;

    private static PredicateExtractionResult ExtractAll(params string[] batches)
    {
        var sql = string.Join("\nGO\n", batches);
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        return TypedPredicateExtractor.Extract(result, catalog, lineage);
    }

    [Fact]
    public void Extract_InsertValuesWithNonAsciiUnicodeLiteralIntoVarchar_FlagsUnicodeReplacement()
    {
        var findings = Extract(
            "CREATE TABLE dbo.T (VarCol VARCHAR(20) NULL);",
            "INSERT INTO dbo.T (VarCol) VALUES (N'日本語');");

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.UnicodeToNonUnicodeReplacement, finding.Kind);
        Assert.Equal("dbo.T", finding.TableQualifiedName);
        Assert.Equal("VarCol", finding.ColumnName);
    }

    [Fact]
    public void Extract_InsertValuesWithAsciiOnlyUnicodeLiteralIntoVarchar_ProvablySafe_NoFinding()
    {

        var findings = Extract(
            "CREATE TABLE dbo.T (VarCol VARCHAR(20) NULL);",
            "INSERT INTO dbo.T (VarCol) VALUES (N'hello');");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_InsertValuesWithUnicodeColumnIntoVarchar_NonLiteral_AlwaysFlagged()
    {

        var findings = Extract(
            "CREATE TABLE dbo.Src (NCol NVARCHAR(20) NULL); CREATE TABLE dbo.Dst (VarCol VARCHAR(20) NULL);",
            "INSERT INTO dbo.Dst (VarCol) SELECT NCol FROM dbo.Src;");

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.UnicodeToNonUnicodeReplacement, finding.Kind);
        Assert.Equal("dbo.Dst", finding.TableQualifiedName);
    }

    [Fact]
    public void Extract_InsertValuesWithFractionalNumericLiteralIntoInt_FlagsNumericScaleNarrowing()
    {

        var findings = Extract(
            "CREATE TABLE dbo.T (IntCol INT NULL);",
            "INSERT INTO dbo.T (IntCol) VALUES (7.9);");

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.NumericScaleNarrowing, finding.Kind);
    }

    [Fact]
    public void Extract_InsertValuesWithFractionalScientificNotationLiteralIntoInt_FlagsApproximateTruncation()
    {

        var findings = Extract(
            "CREATE TABLE dbo.T (IntCol INT NULL);",
            "INSERT INTO dbo.T (IntCol) VALUES (7.9e0);");

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.ApproximateToExactTruncation, finding.Kind);
    }

    [Fact]
    public void Extract_InsertValuesWithWholeNumberDecimalLiteralIntoInt_ProvablySafe_NoFinding()
    {
        var findings = Extract(
            "CREATE TABLE dbo.T (IntCol INT NULL);",
            "INSERT INTO dbo.T (IntCol) VALUES (7.0);");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_InsertValuesWithHigherScaleDecimalLiteral_FlagsNumericScaleNarrowing()
    {
        var findings = Extract(
            "CREATE TABLE dbo.T (DecCol DECIMAL(10,2) NULL);",
            "INSERT INTO dbo.T (DecCol) VALUES (123.456);");

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.NumericScaleNarrowing, finding.Kind);
    }

    [Fact]
    public void Extract_InsertValuesWithinTargetScale_ProvablySafe_NoFinding()
    {
        var findings = Extract(
            "CREATE TABLE dbo.T (DecCol DECIMAL(10,2) NULL);",
            "INSERT INTO dbo.T (DecCol) VALUES (123.40);");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_InsertValuesWithDateTimeLiteralIntoDate_FlagsTemporalPrecisionLoss()
    {
        var findings = Extract(
            "CREATE TABLE dbo.T (DateCol DATE NULL);",
            "INSERT INTO dbo.T (DateCol) VALUES ('2024-01-15 13:45:00');");

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.TemporalPrecisionLoss, finding.Kind);
    }

    [Fact]
    public void Extract_InsertValuesWithDateOnlyLiteralIntoDate_ProvablySafe_NoFinding()
    {
        var findings = Extract(
            "CREATE TABLE dbo.T (DateCol DATE NULL);",
            "INSERT INTO dbo.T (DateCol) VALUES ('2024-01-15');");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_InsertValuesWithSafeTypePair_NoFinding()
    {
        var findings = Extract(
            "CREATE TABLE dbo.T (IntCol INT NULL);",
            "INSERT INTO dbo.T (IntCol) VALUES (7);");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_InsertWithoutExplicitColumnList_PairsPositionallyByDeclaredOrder()
    {
        var findings = Extract(
            "CREATE TABLE dbo.T (IntCol INT NULL, VarCol VARCHAR(20) NULL);",
            "INSERT INTO dbo.T VALUES (1, N'日本語');");

        var finding = Assert.Single(findings);
        Assert.Equal("VarCol", finding.ColumnName);
    }

    [Fact]
    public void Extract_InsertWithDefaultKeyword_SkippedWithoutFinding()
    {
        var findings = Extract(
            "CREATE TABLE dbo.T (IntCol INT NOT NULL DEFAULT 0);",
            "INSERT INTO dbo.T (IntCol) VALUES (DEFAULT);");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_UpdateSetWithLossyLiteral_FlagsWriteLoss()
    {
        var findings = Extract(
            "CREATE TABLE dbo.T (DecCol DECIMAL(10,2) NULL);",
            "UPDATE dbo.T SET DecCol = 123.456 WHERE DecCol IS NULL;");

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.NumericScaleNarrowing, finding.Kind);
        Assert.Equal("dbo.T", finding.TableQualifiedName);
        Assert.Equal("DecCol", finding.ColumnName);
    }

    [Fact]
    public void Extract_UpdateSetFromJoinedTable_ResolvesSourceThroughFromClause()
    {
        var findings = Extract(
            """
            CREATE TABLE dbo.Target (Id INT NOT NULL, VarCol VARCHAR(20) NULL);
            CREATE TABLE dbo.Src (Id INT NOT NULL, NCol NVARCHAR(20) NULL);
            """,
            "UPDATE t SET t.VarCol = s.NCol FROM dbo.Target t JOIN dbo.Src s ON s.Id = t.Id;");

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.UnicodeToNonUnicodeReplacement, finding.Kind);
        Assert.Equal("dbo.Target", finding.TableQualifiedName);
    }

    [Fact]
    public void Extract_UpdateSetSafeAssignment_NoFinding()
    {
        var findings = Extract(
            "CREATE TABLE dbo.T (IntCol INT NULL);",
            "UPDATE dbo.T SET IntCol = 7 WHERE IntCol IS NULL;");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_InsertSelectColumnToColumn_FlagsWriteLossThroughSelectList()
    {
        var findings = Extract(
            """
            CREATE TABLE dbo.Src (Amount DECIMAL(10,4) NOT NULL);
            CREATE TABLE dbo.Dst (Amount DECIMAL(10,2) NULL);
            """,
            "INSERT INTO dbo.Dst (Amount) SELECT Amount FROM dbo.Src;");

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.NumericScaleNarrowing, finding.Kind);
        Assert.Equal("dbo.Dst", finding.TableQualifiedName);
    }

    [Fact]
    public void Extract_InsertSelectWithWhereClause_StillFindsPredicateAndWriteLoss()
    {

        var result = ExtractAll(
            """
            CREATE TABLE dbo.Src (Amount DECIMAL(10,4) NOT NULL, Flag INT NOT NULL);
            CREATE TABLE dbo.Dst (Amount DECIMAL(10,2) NULL);
            """,
            "INSERT INTO dbo.Dst (Amount) SELECT Amount FROM dbo.Src WHERE Flag = 1;");

        Assert.Single(result.WriteLossFindings);
    }

    [Fact]
    public void Extract_InsertSelectStar_LedgeredNotAnalyzed()
    {
        var result = ExtractAll(
            """
            CREATE TABLE dbo.Src (Amount DECIMAL(10,4) NOT NULL);
            CREATE TABLE dbo.Dst (Amount DECIMAL(10,2) NULL);
            """,
            "INSERT INTO dbo.Dst SELECT * FROM dbo.Src;");

        Assert.Empty(result.WriteLossFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "write source");
    }

    [Fact]
    public void Extract_InsertSelectUnion_LedgeredNotAnalyzed()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.Dst (Amount DECIMAL(10,2) NULL);",
            "INSERT INTO dbo.Dst (Amount) SELECT 123.456 UNION SELECT 1.00;");

        Assert.Empty(result.WriteLossFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "write source");
    }

    [Fact]
    public void Extract_InsertIntoUnresolvedTable_LedgeredNotAnalyzed()
    {
        var result = ExtractAll("INSERT INTO dbo.NeverDeclared (Col) VALUES (1);");

        Assert.Empty(result.WriteLossFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "write target");
    }

    [Fact]
    public void Extract_InsertWithUnresolvedTargetColumn_LedgeredNotAnalyzed()
    {
        var result = ExtractAll(
            "CREATE TABLE dbo.T (IntCol INT NULL);",
            "INSERT INTO dbo.T (NeverDeclaredCol) VALUES (1);");

        Assert.Empty(result.WriteLossFindings);
        Assert.Contains(result.SkippedConstructs, s => s.ConstructKind == "write target");
    }

    [Fact]
    public void Extract_InsertWithCteContainingSelectStar_DoesNotThrow()
    {

        var findings = Extract(
            "CREATE TABLE dbo.Src (a INT NULL, b INT NULL, c INT NULL); CREATE TABLE dbo.T (a INT NULL, b INT NULL, c INT NULL);",
            "WITH cte AS (SELECT * FROM dbo.Src) INSERT INTO dbo.T (a, b, c) SELECT a, b, c FROM cte;");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_InsertWithCteContainingSelectStar_LossySource_FlagsWriteLoss()
    {

        var findings = Extract(
            "CREATE TABLE dbo.Src (NCol NVARCHAR(20) NULL); CREATE TABLE dbo.T (VarCol VARCHAR(20) NULL);",
            "WITH cte AS (SELECT * FROM dbo.Src) INSERT INTO dbo.T (VarCol) SELECT NCol FROM cte;");

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.UnicodeToNonUnicodeReplacement, finding.Kind);
        Assert.Equal("dbo.T", finding.TableQualifiedName);
        Assert.Equal("VarCol", finding.ColumnName);
    }

    [Fact]
    public void Extract_SetVariableWithLossyLiteral_FlagsWriteLoss()
    {
        var findings = Extract("DECLARE @v DECIMAL(10,2); SET @v = 123.456;");

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.NumericScaleNarrowing, finding.Kind);
        Assert.Null(finding.TableQualifiedName);
        Assert.Equal("@v", finding.ColumnName);
    }

    [Fact]
    public void Extract_SetVariableWithinDeclaredScale_NoFinding()
    {
        var findings = Extract("DECLARE @v DECIMAL(10,2); SET @v = 123.40;");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_SetVariableFromScalarSubquery_NonLiteral_AlwaysFlagged()
    {
        var findings = Extract(
            "CREATE TABLE dbo.T (NCol NVARCHAR(20) NULL);",
            "DECLARE @v VARCHAR(20); SET @v = (SELECT TOP 1 NCol FROM dbo.T);");

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.UnicodeToNonUnicodeReplacement, finding.Kind);
        Assert.Equal("@v", finding.ColumnName);
    }

    [Fact]
    public void Extract_SetVariableFromWiderVariable_FlagsLengthTruncation()
    {
        var findings = Extract("DECLARE @src VARCHAR(10) = 'HelloWorld'; DECLARE @v VARCHAR(3); SET @v = @src;");

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.LengthTruncation, finding.Kind);
        Assert.Null(finding.TableQualifiedName);
        Assert.Equal("@v", finding.ColumnName);
    }

    [Fact]
    public void Extract_UpdateColumnFromWiderVariable_TableColumnTarget_NeverFlagsLengthTruncation()
    {
        var findings = Extract(
            "CREATE TABLE dbo.T (Col VARCHAR(3) NULL);",
            "DECLARE @src VARCHAR(10) = 'HelloWorld'; UPDATE dbo.T SET Col = @src;");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_SetVariableFromStringAggWithNoMaxOperand_FlagsLengthTruncation()
    {
        var findings = Extract(
            "CREATE TABLE dbo.T (Col VARCHAR(10) NULL);",
            "DECLARE @v VARCHAR(20); SET @v = (SELECT STRING_AGG(Col, ',') FROM dbo.T);");

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.LengthTruncation, finding.Kind);
        Assert.Null(finding.TableQualifiedName);
        Assert.Equal("@v", finding.ColumnName);
        Assert.Equal(8000, finding.SourceType.Length);
    }

    [Fact]
    public void Extract_SetVariableFromStringAggWithMaxOperand_NoFinding()
    {
        var findings = Extract(
            "CREATE TABLE dbo.T (Col VARCHAR(MAX) NULL);",
            "DECLARE @v VARCHAR(20); SET @v = (SELECT STRING_AGG(Col, ',') FROM dbo.T);");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_SetVariableUndeclared_LedgeredOrSkippedWithoutThrowing()
    {
        var findings = Extract("SET @never_declared = 123.456;");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_SetVariableCompoundAssignmentWithLossyLiteral_FlagsWriteLoss()
    {
        var findings = Extract("DECLARE @v DECIMAL(10,2) = 0; SET @v += 123.456;");

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.NumericScaleNarrowing, finding.Kind);
        Assert.Null(finding.TableQualifiedName);
        Assert.Equal("@v", finding.ColumnName);
    }

    [Fact]
    public void Extract_SetVariableCompoundAssignmentWithinScale_NoFinding()
    {
        var findings = Extract("DECLARE @v DECIMAL(10,2) = 0; SET @v += 1;");

        Assert.Empty(findings);
    }

    [Fact]
    public void Extract_UpdateSetCompoundAssignmentWithLossyLiteral_FlagsWriteLoss()
    {
        var findings = Extract(
            "CREATE TABLE dbo.T (DecCol DECIMAL(10,2) NULL);",
            "UPDATE dbo.T SET DecCol += 123.456 WHERE DecCol IS NOT NULL;");

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.NumericScaleNarrowing, finding.Kind);
        Assert.Equal("dbo.T", finding.TableQualifiedName);
        Assert.Equal("DecCol", finding.ColumnName);
    }

    [Fact]
    public void Extract_UpdateSetCompoundAssignmentWithinScale_NoFinding()
    {
        var findings = Extract(
            "CREATE TABLE dbo.T (DecCol DECIMAL(10,2) NULL);",
            "UPDATE dbo.T SET DecCol += 1 WHERE DecCol IS NOT NULL;");

        Assert.Empty(findings);
    }
}
