using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class ExpressionTypeInferencerPipelineTests : OracleTestFixture
{
    private const string CoalesceSql = """
        CREATE TABLE dbo.Accounts (Code varchar(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, INDEX IX_Code (Code));
        GO
        CREATE PROCEDURE dbo.usp_FindAccount @VarcharParam varchar(20), @NVarcharParam nvarchar(20) AS
            SELECT Code FROM dbo.Accounts WHERE Code = COALESCE(@VarcharParam, @NVarcharParam);
        """;

    protected override string DatabaseNameSeed => nameof(ExpressionTypeInferencerPipelineTests);

    protected override string Ddl => CoalesceSql;

    [Fact]
    public async Task VarcharColumnAgainstCoalesceOfVarcharAndNvarcharParams_ClassifiesScanForced_OracleConfirmed()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(CoalesceSql, "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Code");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public void NullIf_DoesNotMergeByPrecedence_ClassifiesUsingFirstExpressionTypeOnly()
    {
        var sql = """
            CREATE TABLE dbo.AccountsNullIf (Code varchar(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, INDEX IX_Code (Code));
            GO
            CREATE PROCEDURE dbo.usp_FindAccountNullIf @VarcharParam varchar(20), @NVarcharParam nvarchar(20) AS
                SELECT Code FROM dbo.AccountsNullIf WHERE Code = NULLIF(@VarcharParam, @NVarcharParam);
            """;

        var parseResult = SqlScriptParser.ParseText("nullif.sql", sql);
        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);
        var result = TypedPredicateExtractor.Extract(parseResult, catalog, lineage);

        var finding = Assert.Single(result.TypedFindings, f => f.Column.ColumnName == "Code");
        Assert.Equal(Verdict.SeekPreserved, finding.Verdict);
    }
}
