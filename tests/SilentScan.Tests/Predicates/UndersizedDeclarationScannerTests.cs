using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates;

public sealed class UndersizedDeclarationScannerTests
{
    private static CatalogColumn Col(string name, SqlTypeCategory category, int length) =>
        new(name, new SqlType(category, Length: length), IsNullable: true, IsIdentity: false, IsComputed: false, IsPersisted: false);

    [Fact]
    public void CatalogColumn_Varchar1_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(new CatalogTable(
            "dbo", "Foo", CatalogTableKind.Table,
            [Col("Flag", SqlTypeCategory.VarChar, 1)], [], SourcePath: "dbo.Foo", SourceLine: 1));

        var findings = UndersizedDeclarationScanner.ScanCatalog(catalog);

        var finding = Assert.Single(findings);
        Assert.Equal(UndersizedDeclarationSite.TableColumn, finding.Site);
        Assert.Equal("dbo.Foo.Flag", finding.QualifiedOrVariableName);
    }

    [Fact]
    public void CatalogColumn_Varchar2_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(new CatalogTable(
            "dbo", "Foo", CatalogTableKind.Table,
            [Col("Code", SqlTypeCategory.NVarChar, 2)], [], SourcePath: "dbo.Foo", SourceLine: 1));

        Assert.Single(UndersizedDeclarationScanner.ScanCatalog(catalog));
    }

    [Fact]
    public void CatalogColumn_Varchar10_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(new CatalogTable(
            "dbo", "Foo", CatalogTableKind.Table,
            [Col("Name", SqlTypeCategory.VarChar, 10)], [], SourcePath: "dbo.Foo", SourceLine: 1));

        Assert.Empty(UndersizedDeclarationScanner.ScanCatalog(catalog));
    }

    [Fact]
    public void CatalogColumn_NonStringType_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(new CatalogTable(
            "dbo", "Foo", CatalogTableKind.Table,
            [new CatalogColumn("Id", new SqlType(SqlTypeCategory.Int), IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false)],
            [], SourcePath: "dbo.Foo", SourceLine: 1));

        Assert.Empty(UndersizedDeclarationScanner.ScanCatalog(catalog));
    }

    private static IReadOnlyList<UndersizedDeclarationFinding> ScanDeclarations(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return UndersizedDeclarationScanner.ScanDeclarations(result, new DatabaseCatalog());
    }

    [Fact]
    public void DeclaredLocalVariable_Char1_Fires()
    {
        var findings = ScanDeclarations("DECLARE @Flag CHAR(1);");

        var finding = Assert.Single(findings);
        Assert.Equal(UndersizedDeclarationSite.Declaration, finding.Site);
        Assert.Equal("@Flag", finding.QualifiedOrVariableName);
    }

    [Fact]
    public void ProcedureParameter_Varchar2_Fires()
    {
        var findings = ScanDeclarations("CREATE PROCEDURE dbo.usp_Foo @Code VARCHAR(2) AS SELECT 1;");

        var finding = Assert.Single(findings);
        Assert.Equal(UndersizedDeclarationSite.Declaration, finding.Site);
        Assert.Equal("@Code", finding.QualifiedOrVariableName);
    }

    [Fact]
    public void DeclaredLocalVariable_Varchar50_NeverFires()
    {
        var findings = ScanDeclarations("DECLARE @Name VARCHAR(50);");

        Assert.Empty(findings);
    }

    [Fact]
    public void DeclaredLocalVariable_Int_NeverFires()
    {
        var findings = ScanDeclarations("DECLARE @Id INT;");

        Assert.Empty(findings);
    }

    [Fact]
    public void MaxTypedVariable_NeverFires()
    {
        var findings = ScanDeclarations("DECLARE @Big VARCHAR(MAX);");

        Assert.Empty(findings);
    }
}
