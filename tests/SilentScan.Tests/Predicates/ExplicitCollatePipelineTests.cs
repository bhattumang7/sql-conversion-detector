using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class ExplicitCollatePipelineTests : OracleTestFixture
{
    private const string DifferingCollateColumnSql = """
        CREATE TABLE dbo.Customers (
            Code varchar(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            INDEX IX_Code (Code));
        GO
        SELECT 1 FROM dbo.Customers WHERE Code COLLATE Latin1_General_CI_AS = 'x';
        """;

    private const string DifferingCollateLiteralSql = """
        CREATE TABLE dbo.CustomersLiteral (
            Code varchar(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            INDEX IX_Code (Code));
        GO
        SELECT 1 FROM dbo.CustomersLiteral WHERE Code = 'x' COLLATE Latin1_General_CI_AS;
        """;

    private const string MatchingCollateColumnSql = """
        CREATE TABLE dbo.CustomersMatchingCollate (
            Code varchar(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            INDEX IX_Code (Code));
        GO
        SELECT 1 FROM dbo.CustomersMatchingCollate WHERE Code COLLATE SQL_Latin1_General_CP1_CI_AS = 'x';
        """;

    private const string MatchingCollateLiteralSql = """
        CREATE TABLE dbo.CustomersLiteralMatchingCollate (
            Code varchar(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            INDEX IX_Code (Code));
        GO
        SELECT 1 FROM dbo.CustomersLiteralMatchingCollate WHERE Code = 'x' COLLATE SQL_Latin1_General_CP1_CI_AS;
        """;

    protected override string DatabaseNameSeed => nameof(ExplicitCollatePipelineTests);

    protected override string Ddl => string.Join(
        "\nGO\n", DifferingCollateColumnSql, DifferingCollateLiteralSql, MatchingCollateColumnSql, MatchingCollateLiteralSql);

    private static async Task<ScanReport> Scan(string sql)
    {
        var report = await EngineAuthoritativeScan.ScanAsync(sql, "SQL_Latin1_General_CP1_CI_AS");
        foreach (var file in report.ParseHealth.Files)
        {
            Assert.Empty(file.Errors);
        }

        return report;
    }

    [Fact]
    public async Task ColumnWithDifferingExplicitCollate_ReportsExpressionDerivedFinding_OracleConfirmed()
    {
        var report = await Scan(DifferingCollateColumnSql);

        var finding = Assert.Single(report.ExpressionDerivedFindings);
        Assert.Equal("Code", finding.ColumnName);
        var underlying = Assert.Single(finding.UnderlyingBaseColumns);
        Assert.Equal("dbo.Customers", underlying.TableQualifiedName);
        Assert.True(underlying.Indexed);
        Assert.Empty(report.TypedFindings);

        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(
            DatabaseName, "SELECT 1 FROM dbo.Customers WHERE Code COLLATE Latin1_General_CI_AS = 'x';");
        Assert.Contains(
            System.Xml.Linq.XDocument.Parse(planXml).Descendants().Where(e => e.Name.LocalName == "Convert"),
            convert => convert.Descendants().Any(e =>
                e.Name.LocalName == "ColumnReference"
                && (string?)e.Attribute("Table") == "[Customers]"
                && (string?)e.Attribute("Column") == "Code"));
    }

    [Fact]
    public async Task ColumnWithMatchingExplicitCollate_IsANoOp_ProducesNoFinding_OracleConfirmed()
    {
        const string probe = "SELECT 1 FROM dbo.CustomersMatchingCollate WHERE Code COLLATE SQL_Latin1_General_CP1_CI_AS = 'x';";
        var report = await Scan(MatchingCollateColumnSql);

        Assert.Empty(report.ExpressionDerivedFindings);
        var summary = report.TypedPredicateSummary;
        Assert.Equal(1, summary.SeekPreservedCount);
        Assert.Empty(report.TypedFindings);

        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);
        var conversions = SilentScan.Verify.Oracle.ConvertImplicitDetector.FindColumnConversions(planXml);
        Assert.DoesNotContain(conversions, c => string.Equals(c.Column, "Code", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LiteralWithDifferingExplicitCollate_ForcesColumnScanForced_OracleConfirmed()
    {
        var report = await Scan(DifferingCollateLiteralSql);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Code");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task LiteralWithMatchingExplicitCollate_IsANoOp_SeekPreserved_OracleConfirmed()
    {
        const string probe = "SELECT 1 FROM dbo.CustomersLiteralMatchingCollate WHERE Code = 'x' COLLATE SQL_Latin1_General_CP1_CI_AS;";
        var report = await Scan(MatchingCollateLiteralSql);

        Assert.Empty(report.TypedFindings);
        Assert.Equal(1, report.TypedPredicateSummary.SeekPreservedCount);

        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);
        var conversions = SilentScan.Verify.Oracle.ConvertImplicitDetector.FindColumnConversions(planXml);
        Assert.DoesNotContain(conversions, c => string.Equals(c.Column, "Code", StringComparison.OrdinalIgnoreCase));
    }

    private static ScanReport ScanParsedOnly(string sql)
    {
        var parseResult = SqlScriptParser.ParseText("collate.sql", sql);
        Assert.Empty(parseResult.Errors);
        var catalog = CatalogBuilder.Build([parseResult], "SQL_Latin1_General_CP1_CI_AS");
        return ScanReportBuilder.BuildFromParseResults([parseResult], catalog);
    }

    [Fact]
    public void ColumnVsColumnDifferingCollations_NoExplicitCollateAnywhere_ReportsCollationConflict()
    {

        var report = ScanParsedOnly("""
            CREATE TABLE dbo.LocalCustomers (
                Email varchar(100) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
                INDEX IX_Email (Email));
            GO
            CREATE TABLE dbo.VendorCustomers (
                Email varchar(100) COLLATE Latin1_General_CI_AS NOT NULL);
            GO
            SELECT 1
            FROM dbo.LocalCustomers l
            INNER JOIN dbo.VendorCustomers v ON l.Email = v.Email;
            """);

        Assert.Empty(report.TypedFindings);
        var conflict = Assert.Single(report.CollationConflictFindings);
        Assert.Equal("dbo.LocalCustomers", conflict.FirstTableQualifiedName);
        Assert.Equal("Email", conflict.FirstColumnName);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", conflict.FirstCollationName);
        Assert.Equal("dbo.VendorCustomers", conflict.SecondTableQualifiedName);
        Assert.Equal("Email", conflict.SecondColumnName);
        Assert.Equal("Latin1_General_CI_AS", conflict.SecondCollationName);
    }

    [Fact]
    public void ConvertResultInheritingColumnCollation_VsDifferentlyCollatedColumn_IsOperandClash()
    {

        var report = ScanParsedOnly("""
            CREATE TABLE dbo.T (Code nvarchar(20) COLLATE Latin1_General_CI_AS NOT NULL, INDEX IX_Code (Code));
            GO
            CREATE TABLE dbo.Raw (Value varchar(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            GO
            SELECT 1 FROM dbo.T, dbo.Raw WHERE Code = CONVERT(nvarchar(20), Value);
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Code");
        Assert.Equal(Verdict.OperandClash, finding.Verdict);
    }

    [Fact]
    public void CrossCategoryColumnVsColumn_DifferingCollations_ReportsCollationConflict()
    {

        var report = ScanParsedOnly("""
            CREATE TABLE dbo.CharSide (Code char(10) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, INDEX IX_Code (Code));
            GO
            CREATE TABLE dbo.VarCharSide (Code varchar(10) COLLATE Latin1_General_CI_AS NOT NULL);
            GO
            SELECT 1 FROM dbo.CharSide c INNER JOIN dbo.VarCharSide v ON c.Code = v.Code;
            """);

        Assert.Empty(report.TypedFindings);
        var conflict = Assert.Single(report.CollationConflictFindings);
        Assert.Equal("dbo.CharSide", conflict.FirstTableQualifiedName);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", conflict.FirstCollationName);
        Assert.Equal("dbo.VarCharSide", conflict.SecondTableQualifiedName);
        Assert.Equal("Latin1_General_CI_AS", conflict.SecondCollationName);
    }
}
