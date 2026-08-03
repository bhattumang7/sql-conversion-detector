using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;

namespace SilentScan.Tests.Reporting;

public sealed class CollationSensitivityReportTests
{
    private static CollationSensitivityReport Analyze(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return CollationSensitivityReport.Analyze([result]);
    }

    [Fact]
    public void Analyze_VarcharColumnVsNvarcharValue_DiffersBetweenCollationFamilies()
    {
        // The flagship rule this whole report exists for: no manifest/DDL collation anywhere,
        // so the bare scan reports Unknown - but a SQL_* database would force a scan while a
        // Windows-collation one would still get a dynamic range seek. Both must be visible.
        var report = Analyze("""
            CREATE TABLE dbo.Accounts (Code varchar(50) NOT NULL);
            SELECT 1 FROM dbo.Accounts WHERE Code = N'x';
            """);

        Assert.Equal(1, report.UnderSqlFamilyAssumption.ScanForcedCount);
        Assert.Equal(0, report.UnderSqlFamilyAssumption.RangeSeekCount);
        Assert.Equal(0, report.UnderSqlFamilyAssumption.UnknownCount);

        Assert.Equal(1, report.UnderWindowsFamilyAssumption.RangeSeekCount);
        Assert.Equal(0, report.UnderWindowsFamilyAssumption.ScanForcedCount);
        Assert.Equal(0, report.UnderWindowsFamilyAssumption.UnknownCount);
    }

    [Fact]
    public void Analyze_UnknownForAnUnrelatedReason_StaysUnknownUnderBothAssumptions()
    {
        // sql_variant is out-of-model regardless of collation - reclassification must leave it
        // alone rather than mistakenly "fixing" every Unknown finding it sees.
        var report = Analyze("""
            CREATE TABLE dbo.Docs (Payload sql_variant NOT NULL);
            SELECT 1 FROM dbo.Docs WHERE Payload = 1;
            """);

        Assert.Equal(1, report.UnderSqlFamilyAssumption.UnknownCount);
        Assert.Equal(1, report.UnderWindowsFamilyAssumption.UnknownCount);
    }

    [Fact]
    public void Analyze_ColumnWithItsOwnExplicitCollation_UnaffectedByEitherAssumption()
    {
        // A column whose collation is already resolved (explicit COLLATE) must classify
        // identically under both assumptions - the assumed collation only fills gaps, it never
        // overrides a real, already-known one.
        var report = Analyze("""
            CREATE TABLE dbo.Accounts (Code varchar(50) COLLATE Latin1_General_CI_AS NOT NULL);
            SELECT 1 FROM dbo.Accounts WHERE Code = N'x';
            """);

        Assert.Equal(report.UnderSqlFamilyAssumption, report.UnderWindowsFamilyAssumption);
        Assert.Equal(1, report.UnderSqlFamilyAssumption.RangeSeekCount);
    }

    [Fact]
    public void Analyze_SeekPreservedFinding_CountedIdenticallyUnderBothAssumptions()
    {
        var report = Analyze("""
            CREATE TABLE dbo.Orders (OrderId int NOT NULL);
            SELECT 1 FROM dbo.Orders WHERE OrderId = 5;
            """);

        Assert.Equal(1, report.UnderSqlFamilyAssumption.SeekPreservedCount);
        Assert.Equal(1, report.UnderWindowsFamilyAssumption.SeekPreservedCount);
        Assert.Equal(0, report.UnderSqlFamilyAssumption.UnknownCount);
    }

    [Fact]
    public void Analyze_UsesCustomCollationNamesWhenSupplied()
    {
        var result = SqlScriptParser.ParseText("test.sql", "CREATE TABLE dbo.T (Id int NOT NULL);");
        var report = CollationSensitivityReport.Analyze([result], "SQL_Latin1_General_Pref_CP850_CI_AS", "French_CI_AS");

        Assert.Equal("SQL_Latin1_General_Pref_CP850_CI_AS", report.SqlFamilyCollation);
        Assert.Equal("French_CI_AS", report.WindowsFamilyCollation);
    }
}
