using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Verify.Catalog;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

/// <summary>
/// <see cref="LiveCatalogReader"/>'s scalar-UDF registry (docs/detection-checklist.md Tier 1
/// #1) - <c>is_schema_bound</c>/<c>is_inlineable</c> straight from <c>sys.sql_modules</c>,
/// oracle-verified against the real engine rather than assumed. The connected Docker instance is
/// SQL Server 2022 (CLAUDE.md), so <c>is_inlineable</c> is always expected to be present here;
/// the pre-2019 fallback path (catching SqlException 207) has no oracle to exercise against on
/// this engine and is covered by its own unit-level reasoning in the reader's doc comment instead.
///
/// The CLR half of this registry has no automated oracle test here, deliberately: loading a real
/// SQLCLR assembly requires a .NET Framework-targeted build this repo's toolchain (.NET 10 SDK
/// only) cannot produce, and no other stream in this codebase deploys one either. The per-
/// function failure-isolation behavior <c>ReadClrScalarUdfInfoAsync</c> depends on
/// (<c>OBJECTPROPERTYEX</c> throwing SqlException 10342 for one unloadable assembly must not
/// blank out every other CLR scalar UDF's data-access info) was instead oracle-verified directly
/// against the local production copy's real EXTERNAL_ACCESS assemblies per CLAUDE.md - see that
/// method's own doc comment in <c>LiveCatalogReader</c> for what the check found and why the
/// reader is shaped the way it is as a result.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class LiveCatalogReaderScalarUdfTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(LiveCatalogReaderScalarUdfTests);

    protected override string Ddl => """
        CREATE FUNCTION dbo.fn_Plain (@x INT)
        RETURNS INT
        AS
        BEGIN
            RETURN @x + 1;
        END;
        GO
        CREATE FUNCTION dbo.fn_Bound (@x INT)
        RETURNS INT
        WITH SCHEMABINDING
        AS
        BEGIN
            RETURN @x + 1;
        END;
        GO
        CREATE FUNCTION dbo.fn_NotInlineable (@x INT)
        RETURNS DATETIME
        AS
        BEGIN
            RETURN GETDATE();
        END;
        """;

    [Fact]
    public async Task ReadAsync_PlainScalarFunction_IsInlineableTrueNotSchemaBound()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Plain", out var info));
        Assert.Equal(ScalarUdfKind.TSql, info!.Kind);
        Assert.False(info.IsSchemaBound);
        Assert.True(info.EngineIsInlineable);
    }

    [Fact]
    public async Task ReadAsync_SchemaBoundFunction_IsSchemaBoundTrue()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_Bound", out var info));
        Assert.True(info!.IsSchemaBound);
    }

    [Fact]
    public async Task ReadAsync_FunctionCallingGetDate_EngineReportsNotInlineable()
    {
        // The engine's own is_inlineable answer must win over a clean static blocker scan - this
        // asserts the ENGINE side of that contract (file-mode's static scan is asserted
        // separately in ScalarUdfInfoTests).
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TryGetScalarUdfInfo("dbo.fn_NotInlineable", out var info));
        Assert.False(info!.EngineIsInlineable);
    }

    [Fact]
    public async Task MergeFileModeExtras_BackfillsBlockerReasonWithoutLosingEngineFlags()
    {
        // Mirrors what LiveScanRunner actually does: read live, then merge a CatalogBuilder pass
        // over the SAME module text (LiveModuleReader's reparse in the real runner) - the merge
        // must keep the engine's own EngineIsInlineable=false (stronger truth) while backfilling
        // the InlineabilityBlocker explanation the live reader itself never computes.
        var liveCatalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var parseResult = SqlScriptParser.ParseText("fn_NotInlineable.sql", """
            CREATE FUNCTION dbo.fn_NotInlineable (@x INT)
            RETURNS DATETIME
            AS
            BEGIN
                RETURN GETDATE();
            END;
            """);
        var fileModeCatalog = CatalogBuilder.Build([parseResult]);

        liveCatalog.MergeFileModeExtras(fileModeCatalog);

        Assert.True(liveCatalog.TryGetScalarUdfInfo("dbo.fn_NotInlineable", out var info));
        Assert.False(info!.EngineIsInlineable);
        Assert.Contains("GETDATE", info.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }
}
