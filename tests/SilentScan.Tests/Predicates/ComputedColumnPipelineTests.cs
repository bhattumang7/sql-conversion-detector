using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Regression coverage for the computed-column type inference gap (formerly pinned in
/// KnownGapCharacterizationTests.ComputedColumn_TypeIsNeverInferred_PredicateIsSilentlyDropped):
/// a predicate against a computed column whose defining expression is trivially inferable
/// (sibling column references, literals, CAST/CONVERT, binary expressions) must classify
/// normally instead of the comparison vanishing with no finding, no Unknown, and no
/// skip-ledger entry. Runs through <see cref="ScanReportBuilder"/>, the same entry point
/// production uses - and, for the verdict-bearing case, against the real SQL Server oracle
/// (CLAUDE.md: verify the real thing, not just that the static pipeline agrees with itself).
/// </summary>
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

        // FullName is NOT persisted, so SQL Server inlines its defining expression at compile
        // time rather than materializing a column named FullName - the plan's ColumnReference
        // nodes only ever name the real underlying columns the expression was built from
        // (discovered running this test against the live oracle: the generic exact-name
        // confirmation in PipelineOracleVerification, which works for every ordinary column,
        // reported NotConfirmed here because "FullName" never appears in the plan at all). The
        // real, oracle-visible signal for a non-persisted computed column's ScanForced verdict
        // is that CONVERT_IMPLICIT lands on one of its constituent base columns instead.
        var probe = "SELECT 1 FROM dbo.People WHERE FullName = N'John Smith';";
        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);
        var conversions = SilentScan.Verify.Oracle.ConvertImplicitDetector.FindColumnConversions(planXml);
        Assert.Contains(conversions, c =>
            string.Equals(c.Table, "People", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(c.Column, "FirstName", StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Column, "LastName", StringComparison.OrdinalIgnoreCase)));
    }

    // ComputedColumnWithUnresolvableExpression_StillReachesTheReport was removed here (roadmap
    // "delete the file-parsed catalog path") - its whole premise (ComputedColumnTypeResolver
    // runs before the UDF return-type registry is built within a single CatalogBuilder.Build
    // pass, so a genuinely-resolvable UDF still stayed Unknown) can no longer occur on ANY
    // remaining production path: a real, persisted table's computed column now comes straight
    // from engine metadata (sys.columns already reports its inferred type, no
    // ComputedColumnTypeResolver pass involved at all), and MergeFileModeExtras never merges a
    // real Table entry's computed columns from the file-mode catalog - only
    // TemporaryTable/TableVariable/TableType entries. The pass-ordering limitation this test
    // pinned is still technically true of CatalogBuilder as an isolated component, but nothing
    // in scan-db or scan-corpus-live can ever reach it through a real persisted table anymore.

    [Fact]
    public async Task ComputedColumnBuiltInFunctionExpression_ClassifiesInsteadOfStayingUnknown()
    {
        // Task #9's closed asymmetry: YEAR() is a curated fixed-return-type builtin
        // (BuiltinFunctionTypeResolver), the SAME table TypedPredicateExtractor and
        // ScalarExpressionResolver already consult - a computed column built from it now
        // classifies a predicate against it (int column vs int literal - SeekPreserved) instead
        // of silently staying Unknown the way it did before ComputedColumnTypeResolver learned
        // to resolve builtin function calls.
        var report = await EngineAuthoritativeScan.ScanAsync(BuiltinFunctionSql, "SQL_Latin1_General_CP1_CI_AS");

        // SeekPreserved findings aren't actionable, so they surface only in the summary count
        // (see ScalarUdfPipelineTests.SameFamilyAndCollationAgainstUdf_ClassifiesSeekPreserved
        // for the identical assertion shape) - Empty(TypedFindings) here is the CORRECT proof
        // the comparison classified (rather than the pre-fix Unknown/ledgered outcome), not a
        // sign it silently vanished.
        Assert.Empty(report.TypedFindings);
        Assert.Equal(1, report.TypedPredicateSummary.SeekPreservedCount);
    }

    [Fact]
    public async Task IsNullExpression_TypesIdenticallyWhetherAsPredicateOperandOrViewColumn_OracleConfirmed()
    {
        // Task #9's own definition of done: ISNULL(NullableCode, N'x') must type the same way -
        // NullableCode's own nvarchar type, per BuiltinFunctionTypeResolver.TakesFirstArgumentType
        // - whether it appears as the non-column operand of a predicate (TypedPredicateExtractor.
        // ResolveFunctionCallOperand) or as a view's SELECT-list column consulted through lineage
        // (ScalarExpressionResolver.ResolveFunctionCall). Code is varchar; comparing it against
        // the nvarchar-typed ISNULL result is a real cross-category, column-converting predicate,
        // so the parity claim is oracle-confirmed, not just internally self-consistent.
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
