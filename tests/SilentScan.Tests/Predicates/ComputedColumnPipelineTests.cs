using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class ComputedColumnPipelineTests : OracleTestFixture
{
    private const string ConcatSql = """
        CREATE TABLE dbo.People (
            FirstName varchar(40) NOT NULL,
            LastName varchar(40) NOT NULL,
            FullName AS (FirstName + ' ' + LastName));
        GO
        SELECT 1 FROM dbo.People WHERE FullName = N'John Smith';
        """;

    private const string BuiltinFunctionSql = """
        CREATE TABLE dbo.Events (
            OccurredAt datetime NOT NULL,
            OccurredYear AS (YEAR(OccurredAt)));
        GO
        SELECT 1 FROM dbo.Events WHERE OccurredYear = 2024;
        """;

    private const string IsNullParitySql = """
        CREATE TABLE dbo.Accounts (
            Code varchar(50) NOT NULL,
            NullableCode nvarchar(50) NULL,
            INDEX IX_Code (Code));
        GO
        CREATE VIEW dbo.vw_Accounts AS SELECT ISNULL(NullableCode, N'x') AS SafeCode FROM dbo.Accounts;
        GO
        SELECT 1 FROM dbo.Accounts WHERE Code = ISNULL(NullableCode, N'x');
        """;

    protected override string DatabaseNameSeed => nameof(ComputedColumnPipelineTests);

    protected override string Ddl => ConcatSql + "\nGO\n" + BuiltinFunctionSql + "\nGO\n" + IsNullParitySql;

    [Fact]
    public async Task StringConcatenationComputedColumn_AgainstNvarcharLiteral_ClassifiesScanForced_OracleConfirmed()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(ConcatSql, "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "FullName");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var probe = "SELECT 1 FROM dbo.People WHERE FullName = N'John Smith';";
        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);
        var conversions = SilentScan.Verify.Oracle.ConvertImplicitDetector.FindColumnConversions(planXml);
        Assert.Contains(conversions, c =>
            string.Equals(c.Table, "People", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(c.Column, "FirstName", StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Column, "LastName", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ComputedColumnBuiltInFunctionExpression_ClassifiesInsteadOfStayingUnknown()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(BuiltinFunctionSql, "SQL_Latin1_General_CP1_CI_AS");

        Assert.Empty(report.TypedFindings);
        Assert.Equal(1, report.TypedPredicateSummary.SeekPreservedCount);
    }

    [Fact]
    public async Task IsNullExpression_TypesIdenticallyWhetherAsPredicateOperandOrViewColumn_OracleConfirmed()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(IsNullParitySql, "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Code");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        var parseResult = SqlScriptParser.ParseText("isnull_parity.sql", IsNullParitySql);
        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = Core.Lineage.LineageResolver.Resolve(catalog, [parseResult]);
        var view = lineage.Find("dbo.vw_Accounts")!;
        var expr = Assert.IsType<Core.Lineage.ColumnProvenance.Expression>(view.FindColumn("SafeCode")!.Provenance);

        Assert.NotNull(expr.InferredType);
        Assert.Equal(SqlTypeCategory.NVarChar, expr.InferredType!.Category);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }
}
