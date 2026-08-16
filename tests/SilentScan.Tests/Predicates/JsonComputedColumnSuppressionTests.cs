using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Corrections-to-shipped-work: JSON_VALUE(col, '$.path') false-positives the shipped
/// function-wrapped-column rule when an indexed computed column with the identical definition
/// exists to seek on instead (SQL Server 2016+). Both the near-miss fixture
/// (FUNCTION_WRAPPED_COLUMN_json_value_clean.sql) - proven through the SAME live/engine-
/// authoritative pipeline production uses, since the matcher reads catalog text that is only
/// ever real when it comes from a live catalog reader (file mode and live mode share the exact
/// same <see cref="SchemaExpressionReference"/> shape) - and the precision guard
/// (FUNCTION_WRAPPED_COLUMN_json_value_different_path_fires.sql, a similar-but-different
/// computed column must NOT suppress) need a real catalog; the plain fires case
/// (FUNCTION_WRAPPED_COLUMN_json_value_fires.sql) is covered catalog-lessly in
/// NonSargablePredicateScannerTests.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class JsonComputedColumnSuppressionTests : OracleTestFixture
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "tier1");

    private static string ReadFixture(string fileName) => File.ReadAllText(Path.Combine(FixturesDir, fileName));

    protected override string DatabaseNameSeed => nameof(JsonComputedColumnSuppressionTests);

    protected override string Ddl => ReadFixture("FUNCTION_WRAPPED_COLUMN_json_value_clean.sql");

    [Fact]
    public void MatchingIndexedComputedColumn_SuppressesTheFinding_FileModeCatalog()
    {
        var sql = ReadFixture("FUNCTION_WRAPPED_COLUMN_json_value_clean.sql");
        var parseResult = SqlScriptParser.ParseText("json_value_clean.sql", sql);
        Assert.False(parseResult.HasErrors);

        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var findings = NonSargablePredicateScanner.Scan(parseResult, catalog, lineage);

        Assert.Empty(findings);
    }

    [Fact]
    public void DifferentPathIndexedComputedColumn_DoesNotSuppress_FileModeCatalog()
    {
        var sql = ReadFixture("FUNCTION_WRAPPED_COLUMN_json_value_different_path_fires.sql");
        var parseResult = SqlScriptParser.ParseText("json_value_different_path_fires.sql", sql);
        Assert.False(parseResult.HasErrors);

        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var findings = NonSargablePredicateScanner.Scan(parseResult, catalog, lineage);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.FunctionWrappedColumn, finding.Kind);
        Assert.Equal("Payload", finding.ColumnName);
        Assert.Equal("JSON_VALUE", finding.Detail);
    }

    [Fact]
    public async Task MatchingIndexedComputedColumn_SuppressesThroughLiveEngineAuthoritativePipeline()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(Ddl);

        Assert.DoesNotContain(report.Tier1Findings, f => f.ColumnName == "Payload");
    }

    [Fact]
    public async Task MatchingIndexedComputedColumn_OracleConfirmsIndexSeek()
    {
        // A literal comparison value, not a declared NVARCHAR(MAX) variable - oracle-verified
        // separately that a MAX-typed comparison value defeats the seek even with the matching
        // indexed computed column present (tracked as its own checklist item, Tier 3 "oversized/
        // MAX-typed parameters"), so this probe must not reuse the generic Tier1ProbeBuilder
        // fallback, which would synthesize exactly that MAX-typed variable from the wrapped
        // column's own declared type and prove the wrong thing.
        const string probe = "SELECT OrderId FROM dbo.Orders WHERE JSON_VALUE(Payload, '$.status') = 'ACTIVE';";

        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public async Task MatchingIndexedComputedColumn_MaxTypedComparisonValue_StillSeeksButThroughGetRangeWithMismatchedTypes()
    {
        // docs/detection-checklist.md Tier 1 "Oversized and MAX-typed parameters" #4: the
        // matched indexed computed column (StatusVal, JSON_VALUE's own bounded NVARCHAR(4000)
        // return type) removes the syntactic FunctionWrappedColumn finding entirely (asserted
        // above), but does NOT make the comparison free - a MAX-typed comparison VALUE still
        // defeats a clean seek on it, per VerdictClassifier.ClassifySameCategory's own oracle-
        // corrected, collation-independent RangeSeek branch (bounded column vs MAX operand).
        // Oracle-confirmed directly: still an Index Seek (never degrades all the way to a scan),
        // but via GetRangeWithMismatchedTypes rather than a plain unmarked seek - the literal-
        // value test above gets the latter, this one must not.
        const string probe = "DECLARE @p NVARCHAR(MAX) = N'ACTIVE'; SELECT OrderId FROM dbo.Orders WHERE JSON_VALUE(Payload, '$.status') = @p;";

        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
        Assert.Contains("GetRangeWithMismatchedTypes", planXml);
    }
}
