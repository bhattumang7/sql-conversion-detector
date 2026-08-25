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

    [Fact]
    public void DecimalColumn_ScaleNarrowedIntoMoney_Fires()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Payment (Amount DECIMAL(18, 6) NOT NULL);
            ALTER TABLE dbo.Payment ALTER COLUMN Amount MONEY;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("Amount", finding.ColumnName);
        Assert.Equal(AlterColumnSafetyKind.PrecisionOrScaleNarrowing, finding.Kind);
    }

    [Fact]
    public void FloatColumn_AlteredToDecimal_Fires()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Measurement (Reading FLOAT NOT NULL);
            ALTER TABLE dbo.Measurement ALTER COLUMN Reading DECIMAL(18, 4);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("Reading", finding.ColumnName);
        Assert.Equal(AlterColumnSafetyKind.PrecisionOrScaleNarrowing, finding.Kind);
    }

    [Fact]
    public void RealColumn_AlteredToFloat_NegativeControl_DoesNotFire()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Measurement (Reading REAL NOT NULL);
            ALTER TABLE dbo.Measurement ALTER COLUMN Reading FLOAT;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void MoneyColumn_AlteredToSmallMoney_NegativeControl_DoesNotFire()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Payment (Amount MONEY NOT NULL);
            ALTER TABLE dbo.Payment ALTER COLUMN Amount SMALLMONEY;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void DecimalColumn_AlteredToFloat_NegativeControl_DoesNotFire()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Payment (Amount DECIMAL(18, 6) NOT NULL);
            ALTER TABLE dbo.Payment ALTER COLUMN Amount FLOAT;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void DateTimeOffsetColumn_AlteredToDateTime2_Fires()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Appointment (ScheduledAt DATETIMEOFFSET NOT NULL);
            ALTER TABLE dbo.Appointment ALTER COLUMN ScheduledAt DATETIME2;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("ScheduledAt", finding.ColumnName);
        Assert.Equal(AlterColumnSafetyKind.TemporalOffsetDropped, finding.Kind);
    }

    [Fact]
    public void DateTimeOffsetColumn_AlteredToDate_Fires()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Appointment (ScheduledAt DATETIMEOFFSET NOT NULL);
            ALTER TABLE dbo.Appointment ALTER COLUMN ScheduledAt DATE;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(AlterColumnSafetyKind.TemporalOffsetDropped, finding.Kind);
    }

    [Fact]
    public void DateTime2Column_AlteredToDateTimeOffset_NegativeControl_DoesNotFire()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Appointment (ScheduledAt DATETIME2 NOT NULL);
            ALTER TABLE dbo.Appointment ALTER COLUMN ScheduledAt DATETIMEOFFSET;
            """);

        Assert.Empty(findings);
    }
}
