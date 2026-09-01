using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class StatementShapeScannerTests
{
    private static IReadOnlyList<StatementShapeFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return StatementShapeScanner.Scan(result);
    }

    private static IReadOnlyList<StatementShapeFinding> ScanCatalog(string ddl)
    {
        var result = SqlScriptParser.ParseText("test.sql", ddl);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        return StatementShapeScanner.ScanCatalog(catalog);
    }

    [Fact]
    public void InsertWithNoColumnList_Fires()
    {
        var findings = Scan("INSERT INTO dbo.T VALUES (1, 2);");

        Assert.Contains(findings, f => f.Kind == StatementShapeFindingKind.InsertWithoutColumnList);
    }

    [Fact]
    public void InsertWithColumnList_NeverFires()
    {
        var findings = Scan("INSERT INTO dbo.T (A, B) VALUES (1, 2);");

        Assert.DoesNotContain(findings, f => f.Kind == StatementShapeFindingKind.InsertWithoutColumnList);
    }

    [Fact]
    public void OrdinalOrderBy_Fires()
    {
        var findings = Scan("SELECT A, B FROM dbo.T ORDER BY 1;");

        Assert.Contains(findings, f => f.Kind == StatementShapeFindingKind.OrdinalOrderBy);
    }

    [Fact]
    public void NamedOrderBy_NeverFiresOrdinal()
    {
        var findings = Scan("SELECT A, B FROM dbo.T ORDER BY A;");

        Assert.DoesNotContain(findings, f => f.Kind == StatementShapeFindingKind.OrdinalOrderBy);
    }

    [Fact]
    public void BareSelectStar_FiresAtLowConfidence()
    {
        var findings = Scan("SELECT * FROM dbo.T;");

        var finding = Assert.Single(findings, f => f.Kind == StatementShapeFindingKind.BareSelectStar);
        Assert.Equal(FindingConfidence.Low, finding.Confidence);
    }

    [Fact]
    public void NamedColumnList_NeverFiresSelectStar()
    {
        var findings = Scan("SELECT A, B FROM dbo.T;");

        Assert.DoesNotContain(findings, f => f.Kind == StatementShapeFindingKind.BareSelectStar);
    }

    [Fact]
    public void ProcedureWithNoSetNocountOn_Fires()
    {
        var findings = Scan("CREATE PROCEDURE dbo.P AS BEGIN SELECT 1; END");

        Assert.Contains(findings, f => f.Kind == StatementShapeFindingKind.MissingSetNocountOn);
    }

    [Fact]
    public void ProcedureWithSetNocountOn_NeverFires()
    {
        var findings = Scan("CREATE PROCEDURE dbo.P AS BEGIN SET NOCOUNT ON; SELECT 1; END");

        Assert.DoesNotContain(findings, f => f.Kind == StatementShapeFindingKind.MissingSetNocountOn);
    }

    [Fact]
    public void ProcedureWithSetNocountOff_StillFiresMissing()
    {
        var findings = Scan("CREATE PROCEDURE dbo.P AS BEGIN SET NOCOUNT OFF; SELECT 1; END");

        Assert.Contains(findings, f => f.Kind == StatementShapeFindingKind.MissingSetNocountOn);
    }

    [Fact]
    public void TriggerWithNoSetNocountOn_Fires()
    {
        var findings = Scan("CREATE TRIGGER dbo.Trg ON dbo.T AFTER INSERT AS BEGIN SELECT 1; END");

        Assert.Contains(findings, f => f.Kind == StatementShapeFindingKind.MissingSetNocountOn);
    }

    [Fact]
    public void TableWithNoPrimaryKey_Fires()
    {
        var findings = ScanCatalog("CREATE TABLE dbo.T (A INT NOT NULL);");

        var finding = Assert.Single(findings, f => f.Kind == StatementShapeFindingKind.TableWithNoPrimaryKey);
        Assert.Equal("dbo.T", finding.ModuleQualifiedName);
        Assert.Contains("no engine-enforced row uniqueness", finding.DetailText);
    }

    [Fact]
    public void TableWithPrimaryKey_NeverFires()
    {
        var findings = ScanCatalog("CREATE TABLE dbo.T (A INT NOT NULL PRIMARY KEY);");

        Assert.DoesNotContain(findings, f => f.Kind == StatementShapeFindingKind.TableWithNoPrimaryKey);
    }

    [Fact]
    public void TableWithUniqueConstraintButNoPrimaryKey_DoesNotClaimNoRowUniqueness()
    {
        var findings = ScanCatalog("CREATE TABLE dbo.T (A INT NOT NULL UNIQUE);");

        var finding = Assert.Single(findings, f => f.Kind == StatementShapeFindingKind.TableWithNoPrimaryKey);
        Assert.DoesNotContain("no engine-enforced row uniqueness", finding.DetailText);
        Assert.Contains("transactional replication or change tracking", finding.DetailText);
    }

    [Fact]
    public void TableWithUniqueIndexButNoPrimaryKey_DoesNotClaimNoRowUniqueness()
    {
        var findings = ScanCatalog(
            "CREATE TABLE dbo.T (A INT NOT NULL); CREATE UNIQUE INDEX IX_T_A ON dbo.T (A);");

        var finding = Assert.Single(findings, f => f.Kind == StatementShapeFindingKind.TableWithNoPrimaryKey);
        Assert.DoesNotContain("no engine-enforced row uniqueness", finding.DetailText);
        Assert.Contains("transactional replication or change tracking", finding.DetailText);
    }

    [Fact]
    public void TableWithFilteredUniqueIndexButNoPrimaryKey_StillClaimsNoRowUniqueness()
    {
        var findings = ScanCatalog(
            "CREATE TABLE dbo.T (A INT NULL); CREATE UNIQUE INDEX IX_T_A ON dbo.T (A) WHERE A IS NOT NULL;");

        var finding = Assert.Single(findings, f => f.Kind == StatementShapeFindingKind.TableWithNoPrimaryKey);
        Assert.Contains("no engine-enforced row uniqueness", finding.DetailText);
    }

    [Fact]
    public void TableWithDisabledUniqueIndexButNoPrimaryKey_StillClaimsNoRowUniqueness()
    {
        var findings = ScanCatalog(
            "CREATE TABLE dbo.T (A INT NOT NULL); CREATE UNIQUE INDEX IX_T_A ON dbo.T (A); ALTER INDEX IX_T_A ON dbo.T DISABLE;");

        var finding = Assert.Single(findings, f => f.Kind == StatementShapeFindingKind.TableWithNoPrimaryKey);
        Assert.Contains("no engine-enforced row uniqueness", finding.DetailText);
    }
}
