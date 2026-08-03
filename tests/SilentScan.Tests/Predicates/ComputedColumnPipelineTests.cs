using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

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

    private const string UnresolvableSql = """
        CREATE FUNCTION dbo.fn_FormatDate(@d datetime) RETURNS varchar(30) AS BEGIN RETURN CONVERT(varchar(30), @d) END;
        GO
        CREATE TABLE dbo.T (
            Created datetime NOT NULL,
            CreatedLabel AS (dbo.fn_FormatDate(Created)));
        GO
        SELECT 1 FROM dbo.T WHERE CreatedLabel = 'x';
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

    protected override string DatabaseName => nameof(ComputedColumnPipelineTests);

    protected override string Ddl => ConcatSql + "\nGO\n" + UnresolvableSql + "\nGO\n" + BuiltinFunctionSql + "\nGO\n" + IsNullParitySql;

    [Fact]
    public async Task StringConcatenationComputedColumn_AgainstNvarcharLiteral_ClassifiesScanForced_OracleConfirmed()
    {
        var parseResult = SqlScriptParser.ParseText("computed_column.sql", ConcatSql);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");

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

    [Fact]
    public void ComputedColumnWithUnresolvableExpression_StillReachesTheReport()
    {
        // dbo.fn_FormatDate is a genuinely registered scalar UDF (CREATE FUNCTION above) - proves
        // the gap is pass-ordering, not non-existence: ComputedColumnTypeResolver runs before the
        // UDF return-type registry is built, so even a real, resolvable-elsewhere function stays
        // Unknown here. Must still surface: either a classified Unknown comparison or a
        // skip-ledger entry - never a comparison that disappears with zero trace. Unknown makes
        // no claim about what the engine actually does, so there is nothing to oracle-confirm here
        // (CLAUDE.md: Unknown means honestly uncertain, never a guess to be double-checked).
        var parseResult = SqlScriptParser.ParseText("computed_column_unresolvable.sql", UnresolvableSql);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");

        var hasUnknownFinding = report.TypedFindings.Any(f => f.Column.ColumnName == "CreatedLabel" && f.Verdict == Verdict.Unknown);
        var hasSkipEntry = report.SkippedConstructs.Any(s => s.ConstructKind == "computed column type" && s.Reason.Contains("CreatedLabel", StringComparison.Ordinal));

        Assert.True(hasUnknownFinding || hasSkipEntry);
    }

    [Fact]
    public void ComputedColumnBuiltInFunctionExpression_ClassifiesInsteadOfStayingUnknown()
    {
        // Task #9's closed asymmetry: YEAR() is a curated fixed-return-type builtin
        // (BuiltinFunctionTypeResolver), the SAME table TypedPredicateExtractor and
        // ScalarExpressionResolver already consult - a computed column built from it now
        // classifies a predicate against it (int column vs int literal - SeekPreserved) instead
        // of silently staying Unknown the way it did before ComputedColumnTypeResolver learned
        // to resolve builtin function calls.
        var parseResult = SqlScriptParser.ParseText("computed_column_builtin_function.sql", BuiltinFunctionSql);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");

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
        var parseResult = SqlScriptParser.ParseText("isnull_parity.sql", IsNullParitySql);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Code");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

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
