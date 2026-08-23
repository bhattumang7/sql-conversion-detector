using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class NamingScannerTests
{
    private static IReadOnlyList<NamingFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return NamingScanner.Scan(result);
    }

    [Fact]
    public void ReservedKeywordAsTableName_Fires()
    {
        var findings = Scan("CREATE TABLE dbo.[order] (Id INT NOT NULL);");

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.ReservedKeywordAsIdentifier);
    }

    [Fact]
    public void OrdinaryTableName_NeverFiresReservedKeyword()
    {
        var findings = Scan("CREATE TABLE dbo.Orders (Id INT NOT NULL);");

        Assert.DoesNotContain(findings, f => f.Kind == NamingFindingKind.ReservedKeywordAsIdentifier);
    }

    [Fact]
    public void ReservedKeywordAsColumnName_Fires()
    {
        var findings = Scan("CREATE TABLE dbo.T (Id INT NOT NULL, [select] INT NULL);");

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.ReservedKeywordAsIdentifier);
    }

    [Fact]
    public void OrdinaryColumnName_NeverFiresReservedKeyword()
    {
        var findings = Scan("CREATE TABLE dbo.T (Id INT NOT NULL, Amount INT NULL);");

        Assert.DoesNotContain(findings, f => f.Kind == NamingFindingKind.ReservedKeywordAsIdentifier);
    }

    [Fact]
    public void ReservedKeywordAsProcedureName_Fires()
    {
        var findings = Scan("CREATE PROCEDURE dbo.[transaction] AS BEGIN SELECT 1; END");

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.ReservedKeywordAsIdentifier);
    }

    [Fact]
    public void ReservedKeywordAsIndexName_Fires()
    {
        var findings = Scan("CREATE TABLE dbo.T (Id INT NOT NULL);\nCREATE INDEX [key] ON dbo.T (Id);");

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.ReservedKeywordAsIdentifier);
    }

    [Fact]
    public void SpPrefixOnProcedure_Fires()
    {
        var findings = Scan("CREATE PROCEDURE dbo.sp_DoSomething AS BEGIN SELECT 1; END");

        var finding = Assert.Single(findings, f => f.Kind == NamingFindingKind.SpPrefixOnUserRoutine);
        Assert.Contains("sp_DoSomething", finding.DetailText);
    }

    [Fact]
    public void SpPrefixOnFunction_Fires()
    {
        var findings = Scan("CREATE FUNCTION dbo.sp_Calculate() RETURNS INT AS BEGIN RETURN 1; END");

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.SpPrefixOnUserRoutine);
    }

    [Fact]
    public void OrdinaryProcedureName_NeverFiresSpPrefix()
    {
        var findings = Scan("CREATE PROCEDURE dbo.DoSomething AS BEGIN SELECT 1; END");

        Assert.DoesNotContain(findings, f => f.Kind == NamingFindingKind.SpPrefixOnUserRoutine);
    }

    [Fact]
    public void UnqualifiedCreateProcedure_Fires()
    {
        var findings = Scan("CREATE PROCEDURE DoSomething AS BEGIN SELECT 1; END");

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.UnqualifiedCreate);
    }

    [Fact]
    public void QualifiedCreateProcedure_NeverFiresUnqualified()
    {
        var findings = Scan("CREATE PROCEDURE dbo.DoSomething AS BEGIN SELECT 1; END");

        Assert.DoesNotContain(findings, f => f.Kind == NamingFindingKind.UnqualifiedCreate);
    }

    [Fact]
    public void UnqualifiedCreateView_Fires()
    {
        var findings = Scan("CREATE VIEW MyView AS SELECT 1 AS Col;");

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.UnqualifiedCreate);
    }

    [Fact]
    public void RedundantDboTypeQualifier_OnParameter_Fires()
    {
        var sql = "CREATE PROCEDURE dbo.P (@p dbo.MyType READONLY) AS BEGIN SELECT 1; END";
        var findings = Scan(sql);

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.RedundantTypeQualifier);
    }

    [Fact]
    public void RedundantDboTypeQualifier_OnDeclare_Fires()
    {
        var findings = Scan("DECLARE @p dbo.MyType;");

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.RedundantTypeQualifier);
    }

    [Fact]
    public void UnqualifiedType_NeverFiresRedundantQualifier()
    {
        var findings = Scan("DECLARE @p MyType;");

        Assert.DoesNotContain(findings, f => f.Kind == NamingFindingKind.RedundantTypeQualifier);
    }

    [Fact]
    public void BuiltInType_NeverFiresRedundantQualifier()
    {
        var findings = Scan("DECLARE @p INT;");

        Assert.DoesNotContain(findings, f => f.Kind == NamingFindingKind.RedundantTypeQualifier);
    }

    [Fact]
    public void NonDboSchemaTypeQualifier_NeverFiresRedundantQualifier()
    {

        var findings = Scan("DECLARE @p custom.MyType;");

        Assert.DoesNotContain(findings, f => f.Kind == NamingFindingKind.RedundantTypeQualifier);
    }
}
