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

    [Fact]
    public void TableColumn_RedundantDboTypeQualifier_Fires()
    {
        var findings = Scan("CREATE TABLE dbo.T (Id dbo.MyType NOT NULL);");

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.RedundantTypeQualifier);
    }

    [Fact]
    public void AlterProcedureSpPrefix_Fires()
    {
        var findings = Scan("ALTER PROCEDURE dbo.sp_DoSomething AS BEGIN SELECT 1; END");

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.SpPrefixOnUserRoutine);
    }

    [Fact]
    public void AlterProcedureOrdinaryName_NeverFiresSpPrefix()
    {
        var findings = Scan("ALTER PROCEDURE dbo.DoSomething AS BEGIN SELECT 1; END");

        Assert.DoesNotContain(findings, f => f.Kind == NamingFindingKind.SpPrefixOnUserRoutine);
    }

    [Fact]
    public void AlterProcedureUnqualified_Fires()
    {
        var findings = Scan("ALTER PROCEDURE DoSomething AS BEGIN SELECT 1; END");

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.UnqualifiedCreate);
    }

    [Fact]
    public void AlterProcedureQualified_NeverFiresUnqualified()
    {
        var findings = Scan("ALTER PROCEDURE dbo.DoSomething AS BEGIN SELECT 1; END");

        Assert.DoesNotContain(findings, f => f.Kind == NamingFindingKind.UnqualifiedCreate);
    }

    [Fact]
    public void AlterFunctionSpPrefix_Fires()
    {
        var findings = Scan("ALTER FUNCTION dbo.sp_Calculate() RETURNS INT AS BEGIN RETURN 1; END");

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.SpPrefixOnUserRoutine);
    }

    [Fact]
    public void AlterFunctionUnqualified_Fires()
    {
        var findings = Scan("ALTER FUNCTION Calculate() RETURNS INT AS BEGIN RETURN 1; END");

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.UnqualifiedCreate);
    }

    [Fact]
    public void CreateFunctionUnqualified_Fires()
    {
        var findings = Scan("CREATE FUNCTION Calculate() RETURNS INT AS BEGIN RETURN 1; END");

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.UnqualifiedCreate);
    }

    [Fact]
    public void QualifiedCreateFunction_NeverFiresUnqualified()
    {
        var findings = Scan("CREATE FUNCTION dbo.Calculate() RETURNS INT AS BEGIN RETURN 1; END");

        Assert.DoesNotContain(findings, f => f.Kind == NamingFindingKind.UnqualifiedCreate);
    }

    [Fact]
    public void CreateViewReservedKeywordName_Fires()
    {
        var findings = Scan("CREATE VIEW dbo.[key] AS SELECT 1 AS Col;");

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.ReservedKeywordAsIdentifier);
    }

    [Fact]
    public void OrdinaryViewName_NeverFiresReservedKeyword()
    {
        var findings = Scan("CREATE VIEW dbo.MyView AS SELECT 1 AS Col;");

        Assert.DoesNotContain(findings, f => f.Kind == NamingFindingKind.ReservedKeywordAsIdentifier);
    }

    [Fact]
    public void AlterViewReservedKeywordName_Fires()
    {
        var findings = Scan("ALTER VIEW dbo.[key] AS SELECT 1 AS Col;");

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.ReservedKeywordAsIdentifier);
    }

    [Fact]
    public void AlterViewUnqualified_Fires()
    {
        var findings = Scan("ALTER VIEW MyView AS SELECT 1 AS Col;");

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.UnqualifiedCreate);
    }

    [Fact]
    public void AlterViewQualified_NeverFiresUnqualified()
    {
        var findings = Scan("ALTER VIEW dbo.MyView AS SELECT 1 AS Col;");

        Assert.DoesNotContain(findings, f => f.Kind == NamingFindingKind.UnqualifiedCreate);
    }

    [Fact]
    public void CreateTriggerReservedKeywordName_Fires()
    {
        var findings = Scan("CREATE TRIGGER dbo.[trigger] ON dbo.T AFTER INSERT AS BEGIN SELECT 1; END");

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.ReservedKeywordAsIdentifier);
    }

    [Fact]
    public void OrdinaryTriggerName_NeverFiresReservedKeyword()
    {
        var findings = Scan("CREATE TRIGGER dbo.T_AfterInsert ON dbo.T AFTER INSERT AS BEGIN SELECT 1; END");

        Assert.DoesNotContain(findings, f => f.Kind == NamingFindingKind.ReservedKeywordAsIdentifier);
    }

    [Fact]
    public void AlterTriggerReservedKeywordName_Fires()
    {
        var findings = Scan("ALTER TRIGGER dbo.[trigger] ON dbo.T AFTER INSERT AS BEGIN SELECT 1; END");

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.ReservedKeywordAsIdentifier);
    }

    [Fact]
    public void SpPrefixCheck_IsCaseInsensitive_Fires()
    {
        var findings = Scan("CREATE PROCEDURE dbo.SP_Foo AS BEGIN SELECT 1; END");

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.SpPrefixOnUserRoutine);
    }

    [Fact]
    public void ReservedKeywordCheck_IsCaseInsensitive_Fires()
    {
        var findings = Scan("CREATE TABLE dbo.T (Id INT NOT NULL, [Select] INT NULL);");

        Assert.Contains(findings, f => f.Kind == NamingFindingKind.ReservedKeywordAsIdentifier);
    }

    [Fact]
    public void IdentifierContainingReservedWordAsSubstring_NeverFires()
    {
        var findings = Scan("CREATE TABLE dbo.T (Id INT NOT NULL, OrderId INT NULL);");

        Assert.DoesNotContain(findings, f => f.Kind == NamingFindingKind.ReservedKeywordAsIdentifier);
    }

    [Fact]
    public void MultipleProcedures_OnlyBadRoutineFlagged_DetailTextNamesCorrectRoutine()
    {
        var sql = "CREATE PROCEDURE dbo.sp_Bad AS BEGIN SELECT 1; END;\n" +
                  "GO\n" +
                  "CREATE PROCEDURE dbo.Good AS BEGIN SELECT 1; END;";
        var findings = Scan(sql);

        var finding = Assert.Single(findings, f => f.Kind == NamingFindingKind.SpPrefixOnUserRoutine);
        Assert.Contains("sp_Bad", finding.DetailText);
        Assert.DoesNotContain("Good\"", finding.DetailText);
    }

    [Fact]
    public void UnqualifiedCreateProcedure_DetailTextNamesProcedureAndOmitsSchema()
    {
        var findings = Scan("CREATE PROCEDURE DoSomething AS BEGIN SELECT 1; END");

        var finding = Assert.Single(findings, f => f.Kind == NamingFindingKind.UnqualifiedCreate);
        Assert.Equal(
            "Procedure \"DoSomething\" is created with no explicit schema qualifier - its real owning schema depends on the connecting principal's own default schema.",
            finding.DetailText);
    }
}
