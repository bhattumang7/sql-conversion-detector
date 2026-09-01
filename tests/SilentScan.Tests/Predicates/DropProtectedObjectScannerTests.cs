using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class DropProtectedObjectScannerTests
{
    private static IReadOnlyList<DropProtectedObjectFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return DropProtectedObjectScanner.Scan(catalog);
    }

    [Fact]
    public void DropSchema_SchemaOwnsTable_Fires()
    {
        var findings = Scan("""
            CREATE SCHEMA Reporting;
            GO
            CREATE TABLE Reporting.MonthlyTotal (TotalId INT NOT NULL);
            DROP SCHEMA Reporting;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(DropProtectedObjectKind.SchemaNotEmpty, finding.Kind);
        Assert.Equal("Reporting", finding.ObjectName);
    }

    [Fact]
    public void DropSchema_SchemaOwnsOnlyAView_Fires()
    {
        var result = SqlScriptParser.ParseText("test.sql", """
            CREATE SCHEMA Reporting;
            GO
            DROP SCHEMA Reporting;
            """);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        catalog.AddViewDefinitionText("Reporting.BaseView", "CREATE VIEW Reporting.BaseView AS SELECT 1 AS Id;");

        var finding = Assert.Single(DropProtectedObjectScanner.Scan(catalog));
        Assert.Equal(DropProtectedObjectKind.SchemaNotEmpty, finding.Kind);
    }

    [Fact]
    public void DropSchema_SchemaOwnsProcedure_Fires()
    {
        var findings = Scan("""
            CREATE SCHEMA Reporting;
            GO
            CREATE PROCEDURE Reporting.DoWork AS SELECT 1;
            GO
            DROP SCHEMA Reporting;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(DropProtectedObjectKind.SchemaNotEmpty, finding.Kind);
    }

    [Fact]
    public void DropSchema_EmptySchema_NegativeControl_DoesNotFire()
    {
        var findings = Scan("""
            CREATE SCHEMA Reporting;
            GO
            DROP SCHEMA Reporting;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void DropSchema_TableDroppedFirst_NegativeControl_DoesNotFire()
    {
        var findings = Scan("""
            CREATE SCHEMA Reporting;
            GO
            CREATE TABLE Reporting.MonthlyTotal (TotalId INT NOT NULL);
            DROP TABLE Reporting.MonthlyTotal;
            DROP SCHEMA Reporting;
            """);

        Assert.Empty(findings);
    }

    [Theory]
    [InlineData("db_owner")]
    [InlineData("db_accessadmin")]
    [InlineData("db_securityadmin")]
    [InlineData("db_ddladmin")]
    [InlineData("db_backupoperator")]
    [InlineData("db_datareader")]
    [InlineData("db_datawriter")]
    [InlineData("db_denydatareader")]
    [InlineData("db_denydatawriter")]
    public void DropRole_FixedDatabaseRole_Fires(string roleName)
    {
        var findings = Scan($"DROP ROLE {roleName};");

        var finding = Assert.Single(findings);
        Assert.Equal(DropProtectedObjectKind.FixedDatabaseRole, finding.Kind);
        Assert.Equal(roleName, finding.ObjectName);
    }

    [Fact]
    public void DropRole_FixedDatabaseRole_IsCaseInsensitive_Fires()
    {
        var findings = Scan("DROP ROLE DB_OWNER;");

        Assert.Single(findings);
    }

    [Fact]
    public void DropRole_CustomRole_NegativeControl_DoesNotFire()
    {
        var findings = Scan("DROP ROLE MyCustomRole;");

        Assert.Empty(findings);
    }
}
