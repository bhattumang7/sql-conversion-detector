using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class TvfCallArgumentMismatchScannerTests
{
    private static IReadOnlyList<TvfCallArgumentMismatchFinding> Scan(params string[] batches)
    {
        var sql = string.Join("\nGO\n", batches);
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        return TvfCallArgumentMismatchScanner.Scan(result, catalog);
    }

    [Fact]
    public void LiteralArgumentNarrowerThanFormalParameter_Fires()
    {
        var findings = Scan(
            "CREATE FUNCTION dbo.fn_ByCode (@Code VARCHAR(10)) RETURNS TABLE AS RETURN (SELECT @Code AS Code);",
            "SELECT * FROM dbo.fn_ByCode('a very long literal value');");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.fn_ByCode", finding.CalleeQualifiedName);
        Assert.Equal("@Code", finding.FormalParameterName);
        Assert.Equal(WriteLossKind.LengthTruncation, finding.Kind);
    }

    [Fact]
    public void VariableArgumentMatchingFormalParameter_NeverFires()
    {
        var findings = Scan(
            "CREATE FUNCTION dbo.fn_ByCode (@Code VARCHAR(10)) RETURNS TABLE AS RETURN (SELECT @Code AS Code);",
            "DECLARE @c VARCHAR(10) = 'abc'; SELECT * FROM dbo.fn_ByCode(@c);");

        Assert.Empty(findings);
    }

    [Fact]
    public void VariableArgumentWiderThanFormalParameter_Fires()
    {
        var findings = Scan(
            "CREATE FUNCTION dbo.fn_ByCode (@Code VARCHAR(3)) RETURNS TABLE AS RETURN (SELECT @Code AS Code);",
            "DECLARE @c VARCHAR(10) = 'abc'; SELECT * FROM dbo.fn_ByCode(@c);");

        var finding = Assert.Single(findings);
        Assert.Equal("@c", finding.CallerExpressionDisplay);
        Assert.Equal(WriteLossKind.LengthTruncation, finding.Kind);
    }

    [Fact]
    public void MultiStatementTableValuedFunction_NeverFires()
    {
        var findings = Scan(
            """
            CREATE FUNCTION dbo.fn_ByCode (@Code VARCHAR(10))
            RETURNS @Result TABLE (Code VARCHAR(10))
            AS
            BEGIN
                INSERT INTO @Result VALUES (@Code);
                RETURN;
            END;
            """,
            "SELECT * FROM dbo.fn_ByCode('a very long literal value');");

        Assert.Empty(findings);
    }

    [Fact]
    public void ColumnReferenceArgument_UnresolvedType_NeverGuesses()
    {
        var findings = Scan(
            "CREATE TABLE dbo.T (Col VARCHAR(200) NULL);",
            "CREATE FUNCTION dbo.fn_ByCode (@Code VARCHAR(10)) RETURNS TABLE AS RETURN (SELECT @Code AS Code);",
            "SELECT * FROM dbo.T CROSS APPLY dbo.fn_ByCode(dbo.T.Col);");

        Assert.Empty(findings);
    }
}
