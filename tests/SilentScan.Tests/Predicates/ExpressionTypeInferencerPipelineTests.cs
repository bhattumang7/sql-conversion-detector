using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Roadmap Phase B: arithmetic and CASE/COALESCE/NULLIF/IIF - CLAUDE.md's own named hard cases -
/// previously always resolved Unknown for lack of any type resolution at all
/// (TypedPredicateExtractor's operand dispatch default arm). Runs through
/// <see cref="ScanReportBuilder"/>, the same entry point production uses, and the verdict-bearing
/// case is confirmed against the real oracle, not just static self-consistency.
/// </summary>
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
        // Oracle-verified (see ExpressionTypeInferencer's own remarks): COALESCE(varchar,
        // nvarchar) resolves nvarchar - the SQL_* collation column then converts, same
        // flagship direction as CLAUDE.md's "varchar column vs nvarchar value" example.
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
        // Static-only regression (NULLIF's asymmetric typing rule is already oracle-verified
        // by ExpressionTypeInferencerTests): NULLIF(@VarcharParam, @NVarcharParam) must type as
        // varchar (expr1's own type), NOT the nvarchar a COALESCE of the same two params would
        // produce - so this predicate must NOT classify ScanForced (same-category, same
        // collation as the column, seek preserved).
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
