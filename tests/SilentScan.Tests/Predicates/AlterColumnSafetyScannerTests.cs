using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class AlterColumnSafetyScannerTests
{
    private static IReadOnlyList<AlterColumnSafetyFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return AlterColumnSafetyScanner.Scan(catalog);
    }

    [Fact]
    public void DecimalColumn_PrecisionNarrowed_Fires()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Invoice (Total DECIMAL(10, 4) NOT NULL);
            ALTER TABLE dbo.Invoice ALTER COLUMN Total DECIMAL(6, 4);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Invoice", finding.TableQualifiedName);
        Assert.Equal("Total", finding.ColumnName);
        Assert.Equal(AlterColumnSafetyKind.PrecisionOrScaleNarrowing, finding.Kind);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void DecimalColumn_ScaleNarrowedOnly_Fires()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Invoice (Total DECIMAL(10, 4) NOT NULL);
            ALTER TABLE dbo.Invoice ALTER COLUMN Total DECIMAL(10, 2);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(AlterColumnSafetyKind.PrecisionOrScaleNarrowing, finding.Kind);
    }

    [Fact]
    public void DecimalColumn_WidenedPrecisionAndScale_NegativeControl_DoesNotFire()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Invoice (Total DECIMAL(10, 4) NOT NULL);
            ALTER TABLE dbo.Invoice ALTER COLUMN Total DECIMAL(12, 6);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void DecimalColumn_UnchangedPrecisionAndScale_NegativeControl_DoesNotFire()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Invoice (Total DECIMAL(10, 4) NOT NULL);
            ALTER TABLE dbo.Invoice ALTER COLUMN Total DECIMAL(10, 4);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void TimeColumn_FractionalSecondsScaleNarrowed_Fires()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Event (StartTime TIME(7) NOT NULL);
            ALTER TABLE dbo.Event ALTER COLUMN StartTime TIME(2);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("StartTime", finding.ColumnName);
        Assert.Equal(AlterColumnSafetyKind.PrecisionOrScaleNarrowing, finding.Kind);
    }

    [Fact]
    public void TimeColumn_FractionalSecondsScaleWidened_NegativeControl_DoesNotFire()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Event (StartTime TIME(2) NOT NULL);
            ALTER TABLE dbo.Event ALTER COLUMN StartTime TIME(7);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void VarcharColumn_AlteredToVarbinary_Fires()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Document (Payload VARCHAR(50) NOT NULL);
            ALTER TABLE dbo.Document ALTER COLUMN Payload VARBINARY(50);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("Payload", finding.ColumnName);
        Assert.Equal(AlterColumnSafetyKind.IncompatibleFamilyConversion, finding.Kind);
    }

    [Fact]
    public void NvarcharColumn_AlteredToVarbinary_Fires()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Document (Payload NVARCHAR(50) NOT NULL);
            ALTER TABLE dbo.Document ALTER COLUMN Payload VARBINARY(50);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(AlterColumnSafetyKind.IncompatibleFamilyConversion, finding.Kind);
    }

    [Fact]
    public void VarbinaryColumn_AlteredToVarchar_NegativeControl_DoesNotFire()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Document (Payload VARBINARY(50) NOT NULL);
            ALTER TABLE dbo.Document ALTER COLUMN Payload VARCHAR(50);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void VarcharColumn_AlteredToNvarchar_NegativeControl_DoesNotFire()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Document (Payload VARCHAR(50) NOT NULL);
            ALTER TABLE dbo.Document ALTER COLUMN Payload NVARCHAR(50);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void VarcharColumn_CollationOnlyChanged_NegativeControl_DoesNotFire()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Document (Payload VARCHAR(50) COLLATE Latin1_General_CI_AS NOT NULL);
            ALTER TABLE dbo.Document ALTER COLUMN Payload VARCHAR(50) COLLATE Latin1_General_CS_AS;
            """);

        Assert.Empty(findings);
    }
}
