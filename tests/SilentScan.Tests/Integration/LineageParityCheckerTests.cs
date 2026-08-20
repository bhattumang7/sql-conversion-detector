using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Integration;

/// <summary>
/// Exercises <see cref="LineageParityChecker"/> directly against the real oracle with
/// hand-built <see cref="LineageCatalog"/>s - CLAUDE.md Verify workflow's "diff inferred view
/// column types/collations against sys.columns; any mismatch is a P0 lineage bug," extended
/// (an audit finding) to cover every provenance kind <see
/// cref="ColumnProvenanceAnalysis.TryGetScalarType"/> resolves a type for, and to diff
/// length/precision/scale, not just category/collation.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class LineageParityCheckerTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanLineageParityCheckerTest";

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;
    private readonly LineageParityChecker _checker;

    public LineageParityCheckerTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
        _checker = new LineageParityChecker(_options);
    }

    public async Task InitializeAsync()
    {
        await _provisioner.CreateFreshAsync(DatabaseName);
        await new ScriptDeployer(_options).DeployAsync(
            """
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, Attribute SQL_VARIANT NULL);
            GO
            CREATE VIEW dbo.vw_Orders AS SELECT OrderCode FROM dbo.Orders;
            GO
            CREATE VIEW dbo.vw_CastOrders AS SELECT CAST(OrderCode AS NVARCHAR(50)) AS OrderCode FROM dbo.Orders;
            GO
            CREATE VIEW dbo.vw_ExprOrders AS SELECT UPPER(OrderCode) AS OrderCode FROM dbo.Orders;
            GO
            CREATE VIEW dbo.vw_VariantOrders AS SELECT Attribute FROM dbo.Orders;
            GO
            """,
            DatabaseName);
    }

    public async Task DisposeAsync() =>
        await _provisioner.DropIfExistsAsync(DatabaseName);

    private static LineageCatalog Catalog(string qualifiedName, string columnName, ColumnProvenance provenance) =>
        new(
            new Dictionary<string, ResolvedRelation>
            {
                [qualifiedName] = new ResolvedRelation(qualifiedName, [new ResolvedColumn(columnName, provenance)]),
            },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new SkipLedger());

    [Fact]
    public async Task CheckAsync_BaseColumnMatchesRealCatalog_NoMismatch()
    {
        var lineage = Catalog(
            "dbo.vw_Orders", "OrderCode",
            new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"))));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task CheckAsync_BaseColumnWrongLength_ReportsLengthMismatch()
    {
        // A category+collation match with the WRONG length: the previous diff would have
        // passed this clean. VARCHAR(50) inferred, but the view's real column is VARCHAR(20).
        var lineage = Catalog(
            "dbo.vw_Orders", "OrderCode",
            new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 50, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"))));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        var mismatch = Assert.Single(mismatches);
        Assert.Equal("length", mismatch.Facet);
        Assert.Equal("50", mismatch.InferredValue);
        Assert.Equal("20", mismatch.ActualValue);
    }

    [Fact]
    public async Task CheckAsync_CastProvenanceMatchesRealView_NoMismatch()
    {
        // Coverage this gate didn't have before: a Cast-provenance column (not a BaseColumn
        // passthrough) is now checked too, using the CAST's own explicit target type. Collation
        // is the source column's own (SQL_Latin1_General_CP1_CI_AS) - CAST-to-string propagates
        // the input's collation in the real engine, verified directly against the oracle.
        var lineage = Catalog(
            "dbo.vw_CastOrders", "OrderCode",
            new ColumnProvenance.Cast(
                new SqlType(SqlTypeCategory.NVarChar, Length: 50, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS")),
                new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20))));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task CheckAsync_CastProvenanceWrongTargetType_ReportsCategoryMismatch()
    {
        var lineage = Catalog(
            "dbo.vw_CastOrders", "OrderCode",
            new ColumnProvenance.Cast(
                new SqlType(SqlTypeCategory.Int),
                new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20))));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        var mismatch = Assert.Single(mismatches);
        Assert.Equal("category", mismatch.Facet);
        Assert.Equal("Int", mismatch.InferredValue);
        Assert.Equal("nvarchar", mismatch.ActualValue);
    }

    [Fact]
    public async Task CheckAsync_SqlVariantBaseColumnMatchesRealCatalog_NoMismatch()
    {
        // sys.types.name for SQL_VARIANT is "sql_variant" (with an underscore) - the checker's
        // own category comparison previously compared against SqlTypeCategory.SqlVariant's bare
        // enum name ("SqlVariant"), which can never match "sql_variant" case-insensitively (the
        // underscore is a real character difference, not a casing one) - a false "category"
        // mismatch on every real SQL_VARIANT column, exactly the false-positive class this
        // gate's own doc comment warns about.
        var lineage = Catalog(
            "dbo.vw_VariantOrders", "Attribute",
            new ColumnProvenance.BaseColumn("dbo.Orders", "Attribute", new SqlType(SqlTypeCategory.SqlVariant)));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task CheckAsync_ExpressionProvenanceWithNoInferredType_SkipsRatherThanGuessing()
    {
        // The checker's own behavior given an ALREADY-null InferredType (constructed directly
        // here, bypassing ScalarExpressionResolver, to isolate the checker from what actually
        // types today) - never guessed at, exactly like the pre-existing Unknown/disagreeing-
        // Union case. NOT a live example of an untyped expression: Pass 2's own
        // BuiltinFunctionTypeResolver already types UPPER/LOWER/LTRIM/RTRIM/REVERSE/REPLACE/
        // LEFT/RIGHT/SUBSTRING/STUFF/MIN/MAX/SUM/AVG/DATEADD (oracle-verified) - an expression
        // this pass genuinely still can't type looks more like FORMAT(...) (locale/format-
        // string rendering, deliberately never modeled - real guess risk).
        var lineage = Catalog(
            "dbo.vw_ExprOrders", "OrderCode",
            new ColumnProvenance.Expression(
                InferredType: null,
                Inputs: [new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20))]));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task CheckAsync_UnionAllBranchesAgree_ChecksTheAgreedType()
    {
        var lineage = Catalog(
            "dbo.vw_Orders", "OrderCode",
            new ColumnProvenance.Union(
            [
                new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 999, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"))),
                new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 999, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"))),
            ]));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        var mismatch = Assert.Single(mismatches);
        Assert.Equal("length", mismatch.Facet);
        Assert.Equal("999", mismatch.InferredValue);
    }

    [Fact]
    public async Task CheckAsync_NullInferredCollation_SkipsFacetRatherThanReportingMismatch()
    {
        // An unpinned repo's column carries Collation=null on purpose (VerdictClassifier's
        // never-guess contract) - sys.columns always reports a real, resolved collation for
        // every string column regardless, so this must never be compared as a mismatch (an
        // audit finding: this previously flagged every unpinned repo's every string column,
        // burying real lineage bugs under 47-of-48 false positives on the DNN Platform corpus).
        var lineage = Catalog(
            "dbo.vw_Orders", "OrderCode",
            new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: null)));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task CheckAsync_UnicodeColumnLength_AccountsForByteDoublingInSysColumns()
    {
        await new ScriptDeployer(_options).DeployAsync(
            "CREATE VIEW dbo.vw_UnicodeCheck AS SELECT CAST(OrderCode AS NVARCHAR(30)) AS OrderCode FROM dbo.Orders;",
            DatabaseName);

        var lineage = Catalog(
            "dbo.vw_UnicodeCheck", "OrderCode",
            new ColumnProvenance.Cast(
                new SqlType(SqlTypeCategory.NVarChar, Length: 30, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS")),
                new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20))));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        // sys.columns.max_length for NVARCHAR(30) is 60 (bytes) - a naive raw-byte comparison
        // against our own character-count Length=30 would falsely report a mismatch here.
        Assert.Empty(mismatches);
    }
}
